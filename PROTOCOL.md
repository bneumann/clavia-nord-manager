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

| library_id | Returns                                      |
|------------|----------------------------------------------|
| `0x01`     | Piano categories: Grand, Upright, Electric, Clav/Hps, Digital, Layer |
| `0x02`     | Bank 1 (single entry "Bank 1", capacity ?)   |
| `0x03`     | Bank 1 (Grand category detail, count 20)     |
| `0x04`     | Bank 1                                       |
| `0x05`     | Samp Lib                                     |
| `0x06`     | Bank 1                                       |
| `0x07`     | Banks A–P (16 entries, 25 slots each)        |
| `0x08`     | Banks 1–8 (Songs / Synths v1, 50 slots each) |
| `0x09`     | Banks 1–8 (Songs / Synths v2, 50 slots each) |

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
| `0x33` | H→D       | EditItemOpen      | payload=[bank, item, category_code] |
| `0x34` | D→H       | EditItemOpenAck   | |

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

## RE captures in this repo

| File | Contents |
|------|----------|
| `detection+readlibrary.pcapng` | Initial discovery + list queries |
| `detection+readlibrary new version.pcapng` | Newer Nord Sound Manager with full iterator enumeration |
| `Upload Test2.pcapng` | Download (export) sequence |
| `Download Stevie Likes It To Nord.pcapng` | Upload (import) sequence |
| `Delete Stevie Likes It.pcapng` | Delete sequence |
| `Swap Bank N22 with N21.pcapng` | Swap sequence |
| `Rename N11 pcapng` | Rename + category change sequence |
