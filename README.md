# Clavia Nord Stage 3 — USB Protocol RE & Linux Manager

This repo reverse-engineers the Clavia Nord Stage 3 USB protocol (`VID 0x0ffc`, `PID 0x0026`) and builds a Linux-native .NET desktop manager (`NordSampleManager/`, Avalonia) to replace the Windows-only Nord Sound Manager. Python scripts (`nord_api.py`, `parse_nord_protocol.py`, `interpret_protocol.py`) are the RE sandbox; confirmed protocol behavior is ported into the C# `NordSampleManager.Protocol` library.

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

```bash
# RE tooling (Python venv with pyusb)
source .venv/bin/activate
python nord_api.py          # live queries to an attached Nord Stage 3
python parse_nord_protocol.py   # offline: tmp.txt → nord_protocol.bin + payloads.txt
python interpret_protocol.py    # pretty-print nord_protocol.bin messages
```

---

## USB endpoints

| Address | Direction | Type      | Used for                          |
|---------|-----------|-----------|-----------------------------------|
| `0x03`  | OUT       | Bulk      | Host → device commands            |
| `0x81`  | IN        | Interrupt | Device → host real-time events    |
| `0x82`  | IN        | Bulk      | Device → host responses / data    |
| `0x04`  | OUT       | Bulk      | (reserved / unused in captures)   |
| `0x84`  | IN        | Bulk      | (reserved / unused in captures)   |

In Wireshark / USBPcap: `1.3.3` = OUT endpoint, `1.3.2` = IN endpoint.  
The host always **pre-posts a read URB** on `0x82` before writing the command to `0x03`. This is a Windows driver artefact — the Linux `libusb` path does a plain write then read.

---

## Frame format

Every message (host→device and device→host) uses the same big-endian framing:

```
Offset  Size  Field
0       4     Total frame length (includes header + payload + CRC)
4       4     Command (CMD_*)
8       4     Param1
12      4     Param2  (query subtype / response type)
16      N     Payload (variable)
len-2   2     CRC-16/IBM-3740 (poly 0x1021, init 0xFFFF, no reflection, final XOR 0)
```

Minimum frame size: 18 bytes (16-byte header + 2-byte CRC, zero-length payload).

---

## Top-level commands (CMD field)

| Value  | Name        | Direction    | Notes                                     |
|--------|-------------|--------------|-------------------------------------------|
| `0x07` | CMD_INIT    | both         | Session open handshake                    |
| `0x06` | CMD_SESSION | both         | Session management (capabilities, close)  |
| `0x0c` | CMD_QUERY   | both         | All library / program queries; Param1 always `0x0a` |

---

## Session lifecycle

### 1. Open session — CMD_INIT (0x07), Param1 = 0x00

```
H→D  cmd=0x07 p1=0x00 p2=0x02  payload=[]
D→H  cmd=0x07 p1=0x00 p2=0x03  payload=[05 06 01 07 00 0a 02 0c 0a 0d 00]
                                          (firmware capabilities / endpoint map)
```

### 2. Device capability exchange — CMD_SESSION (0x06), Param1 = 0x01

Immediately after CMD_INIT the original software sends three CMD_SESSION sub-commands:

| H→D p2 | D→H p2 | Purpose                                                |
|--------|--------|--------------------------------------------------------|
| `0x04` | `0x05` | Request device capabilities; response contains channel count, version |
| `0x00` | `0x01` | Query device status; response = `00000000`             |
| `0x06` | —      | Send display message (e.g. "Synchronizing…"); no response expected |

The C# client currently skips these and goes straight to library queries — this works for all confirmed queries.

### 3. Close session — CMD_SESSION (0x06), Param1 = 0x01

**Must be sent before releasing the USB interface.** Without it the device refuses new connections until power-cycled.

```
H→D  cmd=0x06 p1=0x01 p2=0x02  payload=[]
D→H  cmd=0x06 p1=0x01 p2=0x03  payload=[00 00 00 00]
```

---

## CMD_QUERY (0x0c) — Param1 always 0x0a

All library and program queries use `CMD_QUERY = 0x0c` with `Param1 = 0x0a`. The query subtype is in Param2.

### List queries (Param2 = 0x02 / response 0x03)

Request: `H→D p2=0x02 payload=[library_id: uint32 BE]`  
Response: `D→H p2=0x03 payload=[library_id, count, {name_len, name, capacity}…]`

Response payload contains a length-prefixed string list. Use `ScanLengthPrefixedStrings` to extract names.

| library_id | Returns                                      |
|------------|----------------------------------------------|
| `0x01`     | Piano categories: Grand, Upright, Electric, Clav/Hps, Digital, Layer |
| `0x02`     | Bank 1 (single entry "Bank 1", capacity ?)   |
| `0x03`     | Bank 1 (Grand category detail, count 20)     |
| `0x04`     | Bank 1                                       |
| `0x05`     | Samp Lib                                     |
| `0x06`     | Bank 1                                       |
| `0x07`     | Banks A–P (16 entries, 25 slots each)        |
| `0x08`     | Banks 1–8 (Songs / Synths v1, 50 slots each)|
| `0x09`     | Banks 1–8 (Songs / Synths v2, 50 slots each)|

### Library catalog (Param2 = 0x00 / response 0x01)

```
H→D  p2=0x00  payload=[]
D→H  p2=0x01  payload=[count_byte, {name_len, name, capacity, flags}…]
```

Returns all available library types on the device (Piano Native, Pedal, SampLib, Program, Synth, Song, Live, Settings, …).

---

## Iterator protocol — per-item enumeration

Used by the original software to enumerate every piano, program, song, etc. individually with full metadata. All frames are CMD_QUERY (0x0c) / Param1 (0x0a).

### Step 1 — Select library and get count

```
H→D  p2=0x04  payload=[library_id: uint32 BE]
D→H  p2=0x05  payload=[0, library_id]

H→D  p2=0x08  payload=[library_id: uint32 BE]
D→H  p2=0x09  payload=[0, total_count, size1, size2, 0, flags]
```

`total_count` is the number of items in the library.

| library_id | Library          | Item file type |
|------------|------------------|----------------|
| `0x01`     | Pianos (npno)    | `npno`         |
| `0x07`     | Programs A–P     | `ns3f`         |
| `0x0b`     | Settings / Live  | `ns3t` / `ns3l`|

### Step 2 — Open a bank (category)

```
H→D  p2=0x20  payload=[bank_index: uint32, 0xFFFFFFFF, 0x00000000]
D→H  p2=0x21  payload=[counter=0, bank_id: uint32, first_item=0: uint32]
```

`bank_index` starts at 0 and increments after each bank is exhausted. `0xFFFFFFFF` means "open next bank automatically". For pianos, bank_index maps to category index (0=Grand, 1=Upright, …).

### Step 3 — Request an item

```
H→D  p2=0x1e  payload=[bank_id: uint32, item_index: uint32]
D→H  p2=0x1f  payload=[see layout below]
```

Optional: request full detail (programs only, gives program-level name and Piano/SampLib references):

```
H→D  p2=0x28  payload=[bank_id: uint32, item_index: uint32]
D→H  p2=0x29  payload=[see layout below]
```

### Step 4 — ACK item, advance to next

```
H→D  p2=0x20  payload=[0x00000000, item_index_received: uint32, 0x00000000]
D→H  p2=0x21  payload=[counter: uint32, bank_id: uint32, next_item: uint32]
```

`counter = 0` → more items available; request `next_item` next.  
`counter = 1` → end of this bank; open the next bank with `bank_index + 1`.

### Step 5 — Close iterator

```
H→D  p2=0x06  payload=[]
D→H  p2=0x07  payload=[00 00 00 00]
```

---

## p2=0x1f payload — basic item metadata

Confirmed from RE of `detection+readlibrary new version.pcapng`.

```
Offset  Size  Field
0       4     0 (reserved)
4       4     bank_id (uint32 BE)
8       4     item_index (uint32 BE)
12      4     file_id — unique identifier for this item
16      4     file_type ASCII: "npno" piano | "ns3f" program | "ns3l" live | "ns3t" settings | "nsmp" sample
20      4     version × 100 (uint32 BE)  e.g. 0x0276 = 630 → "6.30"
24      4     hash / timestamp
28      4     0xFFFFFFFF for pianos; varies for programs (category field)
32      4     name_length (uint32 BE)
36      N     ASCII patch/piano name  (N = name_length bytes, no null terminator)
36+N    4     (additional fields: timestamp, flags)
```

**Examples:**
- Piano item 0: `file_type=npno, version=0x0276 (6.30), name="White Grand XL 6.3"`
- Program item 0, bank 0: `file_type=ns3f, version=0x0130 (3.04), name="Royal Grand 3D"`

---

## p2=0x29 payload — program detail (Piano A / Piano B references)

```
Offset  Size  Field
0       4     0 (reserved)
4       4     bank_id (uint32 BE)
8       4     item_index (uint32 BE)
12      4     0x00000004 (constant)
16      1     piano_a_active: 1 = Piano A layer is enabled in this program, 0 = not used (e.g. organ)
17      15    padding / unknown
32      1     piano_a_name_length (byte)
33      N     ASCII piano name for Piano A slot (e.g. "White Grand XL 6.30")
33+N    …     more fields (Piano B references, category, flags)
```

**Note:** `piano_a_active = 0` does not mean the slot is empty — it means the program does not use the Piano A engine (e.g. Hammond organ programs). The patch name comes from p2=0x1f, not p2=0x29.

**p2=0x29 appears twice in the payload** for the same item — once for Piano A and once for Piano B slot references.

---

## Piano detail query (legacy / single-shot)

An older query form that retrieves a single piano by category and location without using the iterator:

```
H→D  CMD_QUERY p1=0x0a p2=0x1e  payload=[category_index: uint32, location: uint32]
D→H  CMD_QUERY p1=0x0a p2=0x1f  payload=[same layout as iterator p2=0x1f above]
```

`p2=0x1e` is dual-use: in iterator context it means "request item", in the legacy context it means "query piano by category/location". The payload determines which interpretation the device uses.

---

## Program / Song single query (legacy)

```
H→D  CMD_QUERY p1=0x0a p2=0x28  payload=[type: uint32, number: uint32]
D→H  CMD_QUERY p1=0x0a p2=0x29  payload=[p2=0x29 detail layout above]
```

`type = 0x00` → query Program number `number`  
`type = 0x01` → query Song number `number`

---

## Known library IDs at a glance

| ID     | p2=0x02 name  | Iterator content     | File type |
|--------|---------------|----------------------|-----------|
| `0x01` | Piano cats    | Individual pianos    | `npno`    |
| `0x05` | Samp Lib      | Sample library       | `nsmp`    |
| `0x07` | Banks A–P     | Programs             | `ns3f`    |
| `0x08` | Banks 1–8 v1  | Songs / Synths       | `ns3s` / `ns3y` |
| `0x09` | Banks 1–8 v2  | Songs / Synths       | `ns3s` / `ns3y` |
| `0x0b` | (Settings)    | Settings / Live      | `ns3t` / `ns3l` |

---

## CRC

All frames end with a 2-byte CRC-16/IBM-3740:
- Polynomial: `0x1021`
- Initial value: `0xFFFF`
- Input reflection: none
- Output reflection: none
- Final XOR: `0x0000`

Computed over all bytes from the start of the frame up to (but not including) the CRC itself.

---

## RE tools

```bash
# Extract bulk data from a Wireshark capture
tshark -r detection+readlibrary.pcapng \
       -Y 'usb.src == "1.3.2" || usb.dst == "1.3.3"' \
       -T fields -e usb.capdata > tmp.txt

# Parse and analyse offline
python parse_nord_protocol.py   # produces nord_protocol.bin, payloads.txt
python interpret_protocol.py    # pretty-print with CRC verification

# Talk to a live device
python nord_api.py
```

Captures in this repo: `detection+readlibrary.pcapng` (original), `detection+readlibrary new version.pcapng` (newer Nord Sound Manager version with full iterator enumeration).
