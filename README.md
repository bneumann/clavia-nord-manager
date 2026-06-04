# Clavia Nord Stage 3 — USB Protocol RE & Linux Manager

Reverse-engineering of the Clavia Nord Stage 3 USB protocol (`VID 0x0ffc`, `PID 0x0026`) and a Linux-native .NET desktop manager (`NordSampleManager/`, Avalonia) to replace the Windows-only Nord Sound Manager.

Python scripts (`nord_api.py`, `parse_nord_protocol.py`, `interpret_protocol.py`) are the RE sandbox; confirmed protocol behavior is ported into the C# `NordSampleManager.Protocol` library.

See **[PROTOCOL.md](PROTOCOL.md)** for the full protocol reference (frame format, all Param2 values, session lifecycle, iterator protocol, download/upload/delete/swap/rename sequences).

---

## Quickstart (Linux)

```bash
# 1) Grant the active user permission to talk to the keyboard
sudo cp 99-nord.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
# re-plug the Nord

# 2) Build and launch the desktop app
dotnet build NordSampleManager.sln
dotnet run --project NordSampleManager
```

Click **Connect & load**. If `LIBUSB_ERROR_ACCESS` appears in the status bar the udev rule is not installed or the device was not re-plugged.

---

## Solution structure

| Project | Purpose |
|---------|---------|
| `NordSampleManager.Protocol` | Headless library: USB transport, framing, `NordClient` façade |
| `NordSampleManager.Protocol.Tests` | xUnit tests for framing, parsing, and `NordClient` command sequences |
| `NordSampleManager` | Avalonia desktop UI (exe) |

```bash
dotnet build NordSampleManager.sln
dotnet test  NordSampleManager.Protocol.Tests
dotnet run  --project NordSampleManager
```

---

## RE tooling

Activate the Python virtualenv before running anything Python (`pyusb` lives there):

```bash
source .venv/bin/activate
```

```bash
# Extract bulk data from a Wireshark capture
tshark -r "detection+readlibrary.pcapng" \
       -Y 'usb.src == "1.3.2" || usb.dst == "1.3.3"' \
       -T fields -e usb.capdata > tmp.txt

# Parse and analyse offline
python parse_nord_protocol.py   # tmp.txt → nord_protocol.bin + payloads.txt + analysis
python interpret_protocol.py    # pretty-print nord_protocol.bin with CRC verification

# Talk to a live device
python nord_api.py
```

`nord_api.py` requires the Nord Stage 3 plugged in and calls `detach_kernel_driver` on interface 0. If `usb.core.find` returns `None` the device is missing or not accessible.

---

## Pipeline

```
Wireshark capture (.pcapng)
        │
        │  tshark extracts bulk USB capdata as hex lines
        ▼
   tmp.txt  ──── parse_nord_protocol.py ────►  nord_protocol.bin
                                          ├──►  payloads.txt
                                          └──►  parsed_protocol.txt

   nord_protocol.bin ──── interpret_protocol.py ────►  pretty-printed messages with CRC verification

   Live Nord Stage 3 over USB ◄────  nord_api.py  (NordProtocol class)
                                ◄────  NordClient  (C#, NordSampleManager.Protocol)
```
