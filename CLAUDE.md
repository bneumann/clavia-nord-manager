# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

Reverse-engineering of the Clavia Nord Stage 3 USB protocol (vendor `0x0ffc`, product `0x0026`). The repo mixes two activities:

1. **Offline analysis** of captured USB traffic (Wireshark `.pcapng` → hex dump → parsed messages) to deduce the protocol.
2. **Live querying** of an attached Nord Stage 3 via `pyusb` to confirm hypotheses against the real device.

A "Nord Sample Manager" application (C#/.NET 10, Avalonia desktop) is being built on top of the confirmed protocol. It is an active work-in-progress, not a stub.

## Protocol shape (essential when editing parsers)

All Nord USB messages share this big-endian frame:

| offset | size | field |
|--------|------|-------|
| 0      | 4    | total length (includes header + payload + CRC) |
| 4      | 4    | command (e.g. `0x0c` QUERY, `0x07` INIT) |
| 8      | 4    | param1 (e.g. `0x0a` PARAM_QUERY) |
| 12     | 4    | param2 (query subtype, e.g. `0x02`/`0x28`/`0x1e`) |
| 16     | N    | payload |
| len-2  | 2    | CRC-16/IBM-3740 (poly `0x1021`, init `0xFFFF`, no reflection, final XOR `0`) |

Payload string records use the pattern `5 zero bytes + 4-byte ID + 4-byte length + ASCII`. Sound-type markers appear at payload offset 16–20 (`npno`=Piano, `nsmp`=Sample, `ns3f`=User, `ns3s`=Songs, `ns3y`=Synth).

USB endpoints used by the device: bulk OUT `0x03` for host→device commands, bulk IN `0x82` for device→host responses (see `PROTOCOL.md` for the full endpoint map). `nord_api.py` discovers the right endpoints by scanning attributes rather than hardcoding addresses.

The full set of known query types and payload meanings (piano categories, banks A–P, banks 1–8, samp lib, programs, songs, piano detail, download, upload, delete, swap, rename, flash compaction, chunked piano install) lives in `PROTOCOL.md` — consult it before adding or renaming any command/payload constants. The nordkeyboards.com REST API used for the online Sound Library is documented in `RE/scripts/SOUNDLIBRARY.md`.

## Pipeline and how the pieces fit together

```
Wireshark capture (detection+readlibrary.pcapng)
        │
        │  tshark extracts bulk USB capdata as hex lines
        ▼
   tmp.txt  ──── parse_nord_protocol.py ────►  nord_protocol.bin    (concatenated raw messages)
                                          ├──►  payloads.txt        (hex + ASCII payloads)
                                          └──►  stdout / parsed_protocol.txt  (full analysis)

   nord_protocol.bin ──── interpret_protocol.py ────►  pretty-printed messages with CRC verification

   Live Nord Stage 3 over USB ◄────  nord_api.py  (NordProtocol class)
```

- `parse_nord_protocol.py` and `interpret_protocol.py` both contain their own copy of `crc16_ibm3740` and a `NordMessage` class — they are intentionally standalone scripts. Keep CRC behavior identical across them and `nord_api.py` if you change one.
- `parse_nord_protocol.py` assumes alternating directions (even index = host→device, odd = device→host). This is a heuristic, not protocol truth.
- `nord_protocol.hexpat` is an ImHex pattern matching the same frame; update it if the header layout changes.
- The big text files at the repo root (`tmp.txt`, `payloads.txt`, `parsed_protocol.txt`, `nord_packets.txt`) are **generated artifacts** committed for inspection — do not hand-edit them, regenerate via the scripts.

## Common commands

Activate the Python virtualenv before running anything Python (only `pyusb` is installed in it):

```bash
source .venv/bin/activate
```

Regenerate `tmp.txt` from a pcapng:

```bash
tshark -r detection+readlibrary.pcapng \
       -Y 'usb.src == "1.3.2" || usb.dst == "1.3.3"' \
       -T fields -e usb.capdata > tmp.txt
```

Run the parsers / API:

```bash
python parse_nord_protocol.py        # tmp.txt -> nord_protocol.bin + payloads.txt + analysis
python interpret_protocol.py [N]     # show first N messages from nord_protocol.bin (default 20)
python nord_api.py                   # talk to a live Nord Stage 3 over USB
python main.py                       # USB device discovery / descriptor read
```

`nord_api.py` requires the Nord Stage 3 plugged in and permission to claim the USB interface (it calls `detach_kernel_driver` on interface 0). If `usb.core.find` returns `None`, the device is missing or not accessible to the current user.

.NET 10 solution (Avalonia desktop app — the end product):

```bash
dotnet build NordSampleManager.sln                              # build all three projects
dotnet test  NordSampleManager.Protocol.Tests                   # run xUnit tests
dotnet run  --project NordSampleManager                         # launch the Avalonia app
```

The solution splits into three projects: `NordSampleManager.Protocol` (headless, USB transport + framing + `NordClient` façade), `NordSampleManager.Protocol.Tests` (xUnit), and `NordSampleManager` (Avalonia UI exe). USB access goes through `LibUsbNordDevice` which mirrors `nord_api.py`'s endpoint-scan and CMD_INIT handshake.

Implemented features: browse Pianos, Programs, Sample Library, Songs, Synths; export / import / delete / swap / rename Programs; browse and install sounds from the nordkeyboards.com online library ("Transfer to Instrument" and "Substitute Selected Sound"). Piano install uses the flash-compaction loop (p2=0x22/0x23/0x26/0x27) and chunked 32 726-byte uploads.

USB permissions: `99-nord.rules` at the repo root grants console users access to VID `0ffc`:PID `0026`. Install with `sudo cp 99-nord.rules /etc/udev/rules.d/ && sudo udevadm control --reload-rules && sudo udevadm trigger`, then re-plug the device. Without it, `LibUsbDotNet` (and `pyusb`) fail with `LIBUSB_ERROR_ACCESS`.

There is no linter or formatter configured beyond `.editorconfig`.

## When extending the protocol knowledge

- New command/payload constants should be added in both `nord_api.py` (`NordProtocol` class) and the lookup dictionaries in `interpret_protocol.py` (`COMMAND_NAMES`, `QUERY_TYPES`) so live and offline tooling agree.
- Document new findings in `PROTOCOL.md` — that is the canonical protocol notebook for this project.

## Deferred implementation notes

### p2=0x35/0x36 Relink operation

Captured in `Relink Imperial to Steinway.pcapng`. The relink sequence updates the piano/sample-lib references stored inside one or more program slots without re-uploading the program file. Full payload layout is not yet decoded. Needed if we want to implement "change which piano a program uses" from within the app.

### Clickable program names in song detail view

When implementing clickable program names (song detail → navigate to / highlight the program in the Programs tab):

**Do not match by name** — two programs can have identical names. The correct approach is file-ref matching:

1. **Song side** (`p2=0x29` payload, per slot): bytes at slot-relative offset 8–14 are a 7-byte file reference. Byte 8 is always `0x07` (Programs library ID); bytes 9–14 are a partial file hash. Store these 7 bytes as `byte[] FileRef` in `SongInfo.Programs` (needs a new `SongProgramRef` record replacing the current `IReadOnlyList<string>`).

2. **Program side** (`p2=0x1f` payload, offset 24–27): 4-byte `hash/timestamp` field. This is the matching field — the song's 7-byte file ref contains this hash in bytes 9–12 (confirmed pattern from `detection+readlibrary new version.pcapng`: slot `0x079cd51e b5…` matches program hash `0x9cd51e??`). Store the hash in `ProgramInfo`.

3. **Lookup**: at display time, match `songSlot.FileRef[1..4]` against `program.FileHash` to find the exact `BankEntry` in `ProgramBanks`, then use its `Ref.Bank` / `Ref.Location` for navigation or display.

The current implementation stores only `IReadOnlyList<string> Programs` (names only) in `SongInfo` and uses `ScanLengthPrefixedStrings` on the `p2=0x29` payload — sufficient for display but not for unambiguous lookup.

## Coding guidelines
Use the .editorconfig for coding guidelines