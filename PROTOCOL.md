# Clavia Nord Stage 3 — USB Protocol Reference

Reverse-engineered from Wireshark captures of the original Nord Sound Manager (Windows).
All confirmed against a live Nord Stage 3 (`VID 0x0ffc`, `PID 0x0026`).

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

### Windows pre-posted reads vs. Linux libusb

USB bulk IN is **host-initiated** — the device cannot push data spontaneously. It can only respond once the host has submitted a read buffer. The original Nord Sound Manager on Windows works asynchronously: it submits an overlapped read URB on `0x82` *before* sending the command on `0x03`, so the response buffer is already waiting the moment the device finishes processing.

```
Windows driver (asynchronous)          Linux libusb (synchronous)

Host                Device             Host                Device
 |                    |                 |                    |
 |--READ_SUBMIT(0x82)→|                 |                    |
 |--WRITE(0x03 cmd)──→|                 |--WRITE(0x03 cmd)──→|
 |                    |                 |←-WRITE_COMPLETE----|
 |←--WRITE_COMPLETE---|                 |                    |
 |                    | (processing)    |                    | (processing)
 |←--READ_COMPLETE----|                 |--READ_SUBMIT(0x82)→|
 |   (response data)  |                 |←--READ_COMPLETE----|
                                        |   (response data)  |
```

In the pcapng captures the read URB appears at a *lower* packet index than the write that caused the response. `parse_nord_protocol.py` uses an alternating-index heuristic (even = H→D, odd = D→H) rather than direction alone because of this interleaving.

The Linux path (`libusb_bulk_transfer`, pyusb, LibUsbDotNet) blocks on each call in sequence — write then read. The device buffers its response internally while the host posts the read, so the small extra round-trip is not a problem in practice. The `NordClient` semaphore enforces that exactly one send+receive pair is in flight at a time, which matches the device's strict request/response model.

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

### CRC

All frames end with a 2-byte CRC-16/IBM-3740:

- Polynomial: `0x1021`
- Initial value: `0xFFFF`
- Input reflection: none
- Output reflection: none
- Final XOR: `0x0000`

Computed over all bytes from the start of the frame up to (but not including) the CRC itself.

---

## Top-level commands (CMD field)

| Value  | Name        | Direction | Notes                                               |
|--------|-------------|-----------|-----------------------------------------------------|
| `0x07` | CMD_INIT    | both      | Session open handshake                              |
| `0x06` | CMD_SESSION | both      | Session management (capabilities, close)            |
| `0x0c` | CMD_QUERY   | both      | All library / program queries; Param1 always `0x0a` |

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

| H→D p2 | D→H p2 | Purpose                                                         |
|--------|--------|-----------------------------------------------------------------|
| `0x04` | `0x05` | Request device capabilities; response contains channel count, version |
| `0x00` | `0x01` | Query device status; response = `00000000`                      |
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

Response payload structure: `[reserved(4)][library_id(4)][count(1)] { [name_len(4)][name(N)][capacity(4)] }…`

| library_id | count | Name(s)              | Capacity | Notes |
|------------|-------|----------------------|----------|-------|
| `0x01`     | 6     | Grand, Upright, Electric, Clav/Hps, Digital, Layer | 20 each | Piano categories |
| `0x02`     | 1     | "Bank 1"             | 100      | Likely Songs (flat view) |
| `0x03`     | 1     | "Bank 1"             | 9        | Likely Live buffers (Stage 3 has 9 Live slots) |
| `0x04`     | 1     | "Bank 1"             | 400      | Likely Programs as flat single bank (16×25=400) |
| `0x05`     | 1     | "Samp Lib"           | 400      | Sample library |
| `0x06`     | 1     | "Bank 1"             | 100      | Likely Songs v2 (same shape as 0x02; purpose unclear) |
| `0x07`     | 16    | "Bank A"–"Bank P"    | 25 each  | Programs (ns3f) |
| `0x08`     | 8     | "Bank 1"–"Bank 8"   | 50 each  | Synths (ns3y); confirmed from HTML export |
| `0x09`     | 8     | "Bank 1"–"Bank 8"   | 50 each  | Songs (ns3s); confirmed from HTML export |

IDs `0x02`, `0x03`, `0x04`, `0x06` all return a single entry named `"Bank 1"` — the names are identical, only the capacity distinguishes them. The `"Bank 1"` label is what the device returns; the Notes column above is inferred from capacity values and not confirmed from captures.

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
D→H  p2=0x09  payload=[reserved(4), total_count(4), storage_a(4), storage_b(4), storage_free_blocks(4), flags(4)]
```

`total_count` is the number of items currently stored in the library.

`storage_total_kb` / `storage_used_kb` — interpretation depends on library type:

- **Program-type libraries (0x07–0x0b):** all return identical values for these fields regardless of per-library item count, confirming they reflect a **shared flash partition** (e.g. 4049 KB total / 4015 KB used on the test device). Unit is KB; 327 programs × ~12 KB/file ≈ 3.9 MB matches 4015 KB used.
- **Synth / Song libraries (0x08, 0x09):** `total_count` (word[1]) is the only meaningful field for storage tracking — it gives the number of occupied slots. Total capacity is fixed at 8 × 50 = 400 slots per library. Observed: Synths = 301/400, Songs = 14/400 on the test device.
- **Sample-heavy libraries (Pianos 0x01, SampLib 0x05):** values are much larger and the unit is **128 KiB allocation blocks**. `storage_total_kb` = used blocks (e.g. 15599 × 128 KiB = 1949.9 MiB for pianos); `storage_used_kb` field is not applicable here — see `sample_storage_mib` below. **Not confirmed** — a second dump from a device with different content would verify.

`sample_storage_mib` — free space in 128 KiB allocation blocks, only meaningful for sample-heavy libraries. Confirmed: 550 blocks × 128 KiB = 68.75 MiB, displayed as "69.0 MB" in Nord Sound Manager (which shows MiB labelled as MB). For program-type libraries this field is 0.

`flags` observed values:
- `0x08` — read-only library (Pianos, Live buffers; factory content, no delete/rename)
- `0x04` — Samp Lib and flat-view Song libraries
- `0x40` — writable program-type libraries (Programs, Songs v1/v2, Synths, Settings); these are exactly the libraries that support delete, swap, rename, upload

| library_id | Library          | Item file type  | Slot capacity |
|------------|------------------|-----------------|---------------|
| `0x01`     | Pianos (npno)    | `npno`          | 6 cats × 20   |
| `0x07`     | Programs A–P     | `ns3f`          | 16 × 25 = 400 |
| `0x08`     | Synths           | `ns3y`          | 8 × 50 = 400  |
| `0x09`     | Songs            | `ns3s`          | 8 × 50 = 400  |
| `0x0b`     | Settings / Live  | `ns3t` / `ns3l` | —             |

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

Optional: request full detail (programs and songs; gives Piano A reference for programs, program list for songs):

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
12      4     file_size_bytes — raw file size in bytes (used as transfer length in download)
16      4     file_type ASCII: "npno" piano | "ns3f" program | "ns3y" synth | "ns3s" song | "nsmp" sample | "ns3l" live | "ns3t" settings
20      4     version × 100 (uint32 BE)  e.g. 0x0276 = 630 → "6.30"
24      4     hash / timestamp
28      4     0xFFFFFFFF for pianos; varies for programs (category field)
32      4     name_length (uint32 BE)
36      N     ASCII patch/piano name  (N = name_length bytes, no null terminator)
36+N    4     (additional fields: timestamp, flags)
```

**Note on size display:** The Nord Sound Manager displays `file_size_bytes / (1024²)` and labels the result "MB" — i.e. it shows MiB as MB. Example: White Grand XL = 213,542,620 bytes = 203.7 MiB, displayed as "203.7 MB".

**Examples:**
- Piano item 0: `file_type=npno, version=0x0276 (6.30), file_size=213,542,620 bytes (203.7 MiB), name="White Grand XL 6.3"`
- Program item 0, bank 0: `file_type=ns3f, version=0x0130 (3.04), name="Royal Grand 3D"`

---

## p2=0x29 payload — item detail

The layout differs by file type.

### Programs (ns3f) — Piano A / Piano B references

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

### Songs (ns3s) — program list

Confirmed from `detection+readlibrary new version.pcapng`. Each song references up to 5 programs. The p2=0x29 response contains 5 variable-length slot records, each structured as:

```
Offset  Size  Field
0       4     reserved (0)
4       4     bank_id (song's bank index)
8       4     item_index (song's location)
12      4     program_count (always 5 for Nord Stage 3)

Per program slot (repeating, variable size):
+0      1     0x01 (slot active flag)
+1      7     0x00…  (zeros)
+8      1     0x07   (library_id for Programs, always 0x07)
+9      7     file_ref (7-byte internal file hash/ID — the Nord Sound Manager
               uses this to show Bank/Location by matching against loaded programs)
+16     1     name_len
+17     N     ASCII program name (N = name_len, no null terminator)
+17+N   1     0x00 (null terminator)
```

**Implementation note:** The program names are directly present in the payload. `ScanLengthPrefixedStrings` on the full p2=0x29 payload reliably extracts all 5 names (confirmed against all 14 songs in the capture — 0 false positives, exact matches against the HTML export).

The 7-byte `file_ref` encodes a device-internal identifier. The Nord Sound Manager resolves it back to a program bank/location by cross-referencing the pre-loaded program list. This lookup is not needed when names are the goal.

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

## Mutating operations

All mutating operations use `CMD_QUERY (0x0c)` / `Param1 (0x0a)` and begin with a `LibrarySelect` (p2=0x04) to select the Programs library (`library_id=7`). All payloads are big-endian uint32 unless noted.

### Delete program (confirmed from `Delete Stevie Likes It.pcapng`)

```
H→D  p2=0x04  payload=[library_id=7]          LibrarySelect
D→H  p2=0x05  payload=[0, library_id]          LibrarySelectAck

H→D  p2=0x14  payload=[bank, item]             DeleteRequest
D→H  p2=0x15  payload=[status, bank, item]     DeleteResponse  (status=0 → success)

H→D  p2=0x08  payload=[library_id=7]           LibraryInfo      (best-effort)
D→H  p2=0x09  payload=[24 bytes]               LibraryInfoAck
H→D  p2=0x06  payload=[]                        CloseIterator    (best-effort)
D→H  p2=0x07  payload=[0]                       CloseIteratorAck
```

`bank` and `item` are the 0-based `SoundRef.Bank` / `SoundRef.Location` values.

### Swap programs (confirmed from `Swap Bank N22 with N21.pcapng`)

```
H→D  p2=0x04  payload=[library_id=7]                      LibrarySelect
D→H  p2=0x05  payload=[0, library_id]                     LibrarySelectAck

H→D  p2=0x1a  payload=[bank1, item1, bank2, item2]        SwapRequest
D→H  p2=0x1b  payload=[status]                            SwapResponse  (status=0 → success)

H→D  p2=0x08  payload=[library_id=7]                      LibraryInfo      (best-effort)
D→H  p2=0x09  payload=[24 bytes]                          LibraryInfoAck
H→D  p2=0x06  payload=[]                                   CloseIterator    (best-effort)
D→H  p2=0x07  payload=[0]                                  CloseIteratorAck
```

Captured example: bank=13 (N), item1=6 (slot N22), item2=5 (slot N21).  
Raw SwapRequest payload: `0000000d 00000006 0000000d 00000005` (all BE uint32).

### Download program (confirmed from `Upload Test2.pcapng`)

The download sequence queries item metadata first to learn the file size, then transfers the raw data.

```
H→D  p2=0x04  payload=[library_id=7]              LibrarySelect
D→H  p2=0x05  payload=[0, library_id]              LibrarySelectAck

H→D  p2=0x1e  payload=[bank, item]                RequestItemBasic
D→H  p2=0x1f  payload=[metadata; see p2=0x1f layout]  ItemBasicData   → read DataSize

H→D  p2=0x0c  payload=[bank, item]                RequestDownload
D→H  p2=0x0d  payload=[0, bank, item]              DownloadReady

H→D  p2=0x12  payload=[bank, item, offset=0, dataSize]  StartTransfer
D→H  p2=0x13  payload=[0, bank, item, 0, dataSize, ...rawData]  FileData

H→D  p2=0x0e  payload=[bank, item]                FinishTransfer
D→H  p2=0x0f  payload=[0, bank, item]              FinishTransferAck
```

The host reassembles the data into a CBIN/ns3f container (44-byte header + raw data).

### Upload program (confirmed from `Download Stevie Likes It To Nord.pcapng`)

Programs are small (~12 KB), so the raw data fits in a single `SendFileData` message.

```
H→D  p2=0x04  payload=[library_id=7]              LibrarySelect
D→H  p2=0x05  payload=[0, library_id]              LibrarySelectAck

H→D  p2=0x0a  payload=[bank, item, size, fileType(4 ASCII), crc32, category, nameLen, name]
                                                   UploadMetadata
D→H  p2=0x0b  payload=[status, bank, item]         UploadMetadataAck

H→D  p2=0x10  payload=[bank, item, offset=0, size, ...rawData]  SendFileData
D→H  p2=0x11  payload=[status, bank, item]          SendFileDataAck

H→D  p2=0x0e  payload=[bank, item]                FinishTransfer
D→H  p2=0x0f  payload=[0, bank, item]              FinishTransferAck
```

`crc32` is CRC-32 of the raw data (not the CBIN wrapper). `category` is the program category code (uint32 BE).

### Install piano (confirmed from `Replace Silver Grand with Steinway from Online Library.pcapng`)

Piano files can be 100–200+ MB and are always downloaded from nordkeyboards.com (library_id=1,
`fileType="npno"`, `categoryCode=0xffffffff`). The raw data is sent in **32 726-byte chunks**.

**Step 1 — Delete existing occupant (if slot is occupied):**

```
H→D  p2=0x04  payload=[library_id=1]              LibrarySelect
D→H  p2=0x05  payload=[0, library_id]              LibrarySelectAck

H→D  p2=0x14  payload=[categoryIndex, slot]        DeleteRequest
D→H  p2=0x15  payload=[status, categoryIndex, slot] DeleteResponse

D→H  CMD_STATUS p2=0x06  "Cleaning…"               (eat — unsolicited status string)
D→H  CMD_STATUS p2=0x07  [0]                        (eat — done status)
```

**Step 2 — Flash compaction loop (always follows a piano delete):**

```
D→H  p2=0x22  payload=[total_steps u32]            FlashCompactNotify  (device-initiated)
H→D  p2=0x23  payload=[0x00000000]                 FlashCompactAck     (no D→H reply)

repeat until is_busy == 0:
  H→D  p2=0x26  payload=[]                          FlashCompactPoll
  D→H  p2=0x27  payload=[0, total_steps, offset, is_busy]  FlashCompactState
```

`is_busy` is word[3] of the p2=0x27 payload; loop exits when it equals `0`.
Confirmed: 67 iterations for a 131 MB piano delete, ~1053 internal flash ops.

**Step 3 — Upload new piano:**

```
H→D  p2=0x0a  payload=[categoryIndex, slot, totalSize, "npno", crc32, 0xffffffff, nameLen, name]
                                                   UploadMetadata   (categoryCode = 0xffffffff)
D→H  p2=0x0b  payload=[status, categoryIndex, slot]  UploadMetadataAck

for each 32726-byte chunk (last chunk may be smaller):
  H→D  p2=0x10  payload=[categoryIndex, slot, offset, chunkSize, ...chunk]  SendFileData
  D→H  p2=0x11  payload=[status, categoryIndex, slot]  SendFileDataAck

H→D  p2=0x0e  payload=[categoryIndex, slot]        FinishTransfer
D→H  p2=0x0f  payload=[0, categoryIndex, slot]     FinishTransferAck
```

The piano file from nordkeyboards.com is a CBIN container (44-byte header + raw data).
The `crc32` and `totalSize` fields are computed from the raw data after stripping the CBIN header.

### Rename program (confirmed from `Rename N11 pcapng`)

The rename sequence also sets the category code in `EditItemOpen`.

```
H→D  p2=0x04  payload=[library_id=7]                      LibrarySelect
D→H  p2=0x05  payload=[0, library_id]                     LibrarySelectAck

H→D  p2=0x33  payload=[bank, item, category_code]         EditItemOpen
D→H  p2=0x34  (ack)                                        EditItemOpenAck

H→D  p2=0x1c  payload=[bank, item, nameLen, name_bytes]   WriteName
D→H  p2=0x2c  (progress notification, may appear 2-3×)    ProgressNotify
D→H  p2=0x1d  (ack)                                        WriteNameAck

H→D  p2=0x06  payload=[]                                   CloseIterator
D→H  p2=0x07  payload=[0]                                  CloseIteratorAck
```

The `p2=0x2c` ProgressNotify frames appear between WriteName and WriteNameAck — the client must keep reading until it receives `p2=0x1d`.

---

## Param2 reference table

| Param2 | Direction | Name              | Notes |
|--------|-----------|-------------------|-------|
| `0x00` | H→D       | LibraryCatalog    | List all library types |
| `0x01` | D→H       | LibraryCatalogAck | |
| `0x02` | H→D       | QueryListBanks    | payload=[library_id] |
| `0x03` | D→H       | QueryListBanksAck | length-prefixed string list |
| `0x04` | H→D       | LibrarySelect     | payload=[library_id] |
| `0x05` | D→H       | LibrarySelectAck  | |
| `0x06` | H→D       | CloseIterator     | empty payload |
| `0x07` | D→H       | CloseIteratorAck  | |
| `0x08` | H→D       | LibraryInfo       | payload=[library_id] |
| `0x09` | D→H       | LibraryInfoAck    | total_count etc. |
| `0x0a` | H→D       | UploadMetadata    | |
| `0x0b` | D→H       | UploadMetadataAck | |
| `0x0c` | H→D       | RequestDownload   | payload=[bank, item] |
| `0x0d` | D→H       | DownloadReady     | |
| `0x0e` | H→D       | FinishTransfer    | |
| `0x0f` | D→H       | FinishTransferAck | |
| `0x10` | H→D       | SendFileData      | payload=[bank, item, offset, size, data] |
| `0x11` | D→H       | SendFileDataAck   | |
| `0x12` | H→D       | StartTransfer     | payload=[bank, item, offset, dataSize] |
| `0x13` | D→H       | FileData          | payload=[0, bank, item, 0, dataSize, rawData] |
| `0x14` | H→D       | DeleteRequest     | payload=[bank, item] |
| `0x15` | D→H       | DeleteResponse    | payload=[status, bank, item]; 0=success |
| `0x1a` | H→D       | SwapRequest       | payload=[bank1, item1, bank2, item2] |
| `0x1b` | D→H       | SwapResponse      | payload=[status]; 0=success |
| `0x1c` | H→D       | WriteName         | payload=[bank, item, nameLen, name] |
| `0x1d` | D→H       | WriteNameAck      | |
| `0x1e` | H→D       | RequestItemBasic  | payload=[bank, item] (also QueryPianoDetail in legacy use) |
| `0x1f` | D→H       | ItemBasicData     | full item metadata; see p2=0x1f layout |
| `0x20` | H→D       | OpenIterator      | open bank or ACK item |
| `0x21` | D→H       | IteratorState     | counter, bank, next_item |
| `0x28` | H→D       | RequestItemDetail | payload=[bank, item] (also QueryProgramOrSong in legacy use) |
| `0x29` | D→H       | ItemDetailData    | program detail; see p2=0x29 layout |
| `0x2c` | D→H       | ProgressNotify    | emitted during rename; keep reading until WriteNameAck |
| `0x22` | D→H       | FlashCompactNotify| payload=[total_steps u32]; sent unsolicited after piano delete |
| `0x23` | H→D       | FlashCompactAck   | payload=[0x00000000]; no D→H reply |
| `0x26` | H→D       | FlashCompactPoll  | empty payload; host polls each compaction step |
| `0x27` | D→H       | FlashCompactState | payload=[0, total, offset, is_busy]; is_busy=0 → done |
| `0x33` | H→D       | EditItemOpen      | payload=[bank, item, category_code] |
| `0x34` | D→H       | EditItemOpenAck   | |

---

## Known library IDs at a glance

| ID     | p2=0x02 name  | Iterator content  | File type       | Capacity      | Confirmed |
|--------|---------------|-------------------|-----------------|---------------|-----------|
| `0x01` | Piano cats    | Individual pianos | `npno`          | 6 cats × 20   | ✓ capture |
| `0x05` | Samp Lib      | Sample library    | `nsmp`          | 1 × 400       | ✓ iterator confirmed |
| `0x07` | Banks A–P     | Programs          | `ns3f`          | 16 × 25 = 400 | ✓ capture |
| `0x08` | Banks 1–8     | Synths            | `ns3y`          | 8 × 50 = 400  | ✓ HTML export + live |
| `0x09` | Banks 1–8     | Songs             | `ns3s`          | 8 × 50 = 400  | ✓ HTML export + live |
| `0x0b` | (Settings)    | Settings / Live   | `ns3t` / `ns3l` | —             | partial   |

---

## Sound category codes

Category codes appear in the `CategoryField` at bytes [28–31] of the `p2=0x1f` (ItemBasicData)
payload. Confirmed by cross-referencing `detection+readlibrary.pcapng` p2=0x1f payloads
against the Windows Sound Manager HTML exports. The same 4-byte code space is shared by
Synths, Sample instruments, and Pianos (where applicable).

### Synth category codes

| Category name    | Code         | Count (HTML) |
|------------------|--------------|-------------|
| Pad Synth        | `0x000a0001` | 46          |
| Bass Synth       | `0x000a0003` | 32          |
| Classic Synth    | `0x000a0004` | 95          |
| Lead Synth       | `0x000a0007` | 35          |
| Analog Strings   | `0x00090003` | 3           |
| Piano            | `0x00080000` | 17          |
| Tuned Percussion | `0x00070001` | 6           |
| Rhythmic         | `0x00110000` | 13          |
| Misc             | `0x000e0000` | 15          |
| Effects          | `0x00040000` | 33          |
| Drums            | `0x00020000` | 6           |

### Sample library category codes

| Category name    | Code         |
|------------------|--------------|
| Bass             | `0x00010000` |
| Accordion/Harm   | `0x00030000` |
| Guitar/Plucked   | `0x00050000` |
| Organ            | `0x00060000` |
| Tuned Percussion | `0x00070001` |
| Piano            | `0x00080000` |
| Solo Strings     | `0x00090001` |
| Ensemble Strings | `0x00090002` |
| Analog Strings   | `0x00090003` |
| Bass Synth       | `0x000a0003` |
| Classic Synth    | `0x000a0004` |
| Lead Synth       | `0x000a0007` |
| Choir            | `0x000b0000` |
| Solo Brass       | `0x000c0001` |
| Ensemble Brass   | `0x000c0002` |
| Orchestral       | `0x000d0000` |
| Misc             | `0x000e0000` |
| Mellotron        | `0x00100000` |

---

## RE captures in this repo

All captures are in `RE/captures/`.

| File | Contents |
|------|----------|
| `detection+readlibrary.pcapng` | Initial discovery + list queries + full iterator enumeration for all libraries |
| `detection+readlibrary new version.pcapng` | Newer Nord Sound Manager — confirms iterator pattern; song detail with program refs |
| `Upload Test2.pcapng` | Download (export) sequence for a program file |
| `Download Stevie Likes It To Nord.pcapng` | Upload (import) sequence for a program file |
| `Delete Stevie Likes It.pcapng` | Delete sequence for a program |
| `Swap Bank N22 with N21.pcapng` | Swap sequence for two programs |
| `Rename N11 pcapng` | Rename + category change sequence |
| `Replace Silver Grand with Steinway from Online Library.pcapng` | Full piano install: delete + flash compaction (p2=0x22–0x27) + chunked upload (32 726-byte chunks, 4 228 total for 131 MB) |
| `Relink Imperial to Steinway.pcapng` | p2=0x35/0x36 relink sequence (program slot → different piano); not yet implemented |
| `Upload Calvinet D6.pcapng` | Piano upload (alternate example) |
