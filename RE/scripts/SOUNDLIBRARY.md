# Nord sample and sound library API

The official Nord Sound Manager downloads sounds from nordkeyboards.com via a REST API.
This file documents the complete findings, confirmed by live API calls on 2026-06-08.

Base URL: `https://www.nordkeyboards.com`

## Keyboard codes

Each keyboard model has a numeric API code used in download URLs. The USB product ID maps to
this code via `KeyboardRegistry` in the app.

| Keyboard          | USB PID | API code |
|-------------------|---------|----------|
| Nord Stage 3      | 0x0026  | 54       |
| Nord Stage 4      | —       | 20       |
| Nord Piano 6      | —       | 2198     |
| Nord Grand 2      | —       | 1766     |
| Nord Electro 7    | —       | 2282     |
| Nord Electro 6    | —       | 60       |
| Nord Piano 5      | —       | 48       |
| Nord Grand        | —       | 66       |
| Nord Piano 4      | —       | 34       |
| Nord Electro 5    | —       | 180      |
| Nord Piano 3      | —       | 186      |
| Nord Stage 2 EX   | —       | 189      |
| Nord Stage 2      | —       | 215      |

---

## 1. Compatible keyboards for a product

```
GET /wt/api/main/v1/compatible_products/{productCode}/
→ {
    "active_products":  [{"label":"Nord Stage 4","value":20}, …],
    "legacy_products":  [{"label":"Nord Stage 3","value":54}, …]
  }
```
`value` is the keyboard code used in download URLs.

---

## 2. Piano library catalog

The piano library is paginated (currently 3 pages). Each page embeds item data in the
`__NEXT_DATA__` JSON script block on the HTML page.

```
GET /sounds/piano-library/?page={N}     N = 1..totalPages
→ HTML containing <script id="__NEXT_DATA__" type="application/json">…</script>
  componentProps.items[]:
    title             "Astoria Grand"
    type              "PianoLibraryPage"
    text              description
    playerData        "https://assets.nordkeyboards.com/…/Cubanera_trim.mp3"
    compatibleProducts "/wt/api/main/v1/compatible_products/2279/"
    pianoDownloads    "/wt/api/main/v1/piano/downloads/2279/"
    latestVersions    {"6":"6.1"}   (internal tag → sound version string)
    tags              [{"tag":"Grand Piano","color":"black"}]
  componentProps.pagination: {"currentPage":N,"totalPages":3}
```

### Piano download options

```
GET /wt/api/main/v1/piano/downloads/{productCode}/
→ {
    "downloads": [
      {"size":"xl","file_size":"208.54 MB",
       "download_url":"/wt/api/main/v1/download/piano_library_download/436/",
       "version":1,"version_name":"6.1"},
      {"size":"l", "file_size":"141.97 MB",
       "download_url":"/wt/api/main/v1/download/piano_library_download/437/", …},
      {"size":"m", …},
      {"size":"s", …}
    ],
    "compatible_products": [{"label":"Nord Stage 3","value":54}, …]
  }
```
Multiple quality levels: XL/L/M/S differ in file size only (same version).

### Piano file download

```
GET /wt/api/main/v1/download/piano_library_download/{fileId}/
→ application/octet-stream
  Content-Disposition: attachment; filename="Astoria Grand XL 6.1.npno"
  Body: CBIN-formatted .npno file (208+ MB for XL)
```
File starts with "CBIN" magic + "npno" — identical to local .npno exports.
Parse with `CbinFile.TryParse()` to extract raw sample data.

---

## 3. Sample library catalog

```
GET /sounds/sample-library/
→ HTML with __NEXT_DATA__ JSON
  componentProps.items[]:
    title             "Accordion/Harmonium"
    type              "SampleLibraryPage"
    text              description
    link.href         "/sounds/sample-library/accordion/"
    accordionItems    "/wt/api/main/v1/sample_instruments_for_sample_library/336/latest/"
    download          "/wt/api/main/v1/download/sample_instruments/{keyboard}/library/336/"
    compatibleProducts "/wt/api/main/v1/compatible_products/336/"
```

### All sample library categories (sample_library_id)

| Category            | sample_library_id |
|---------------------|-------------------|
| Accordion/Harmonium | 336               |
| Bass                | 332               |
| Brass               | 2046              |
| Guitar/Plucked      | 337               |
| Mellotron/Chamberlin| 338               |
| Orchestral          | 339               |
| Organ               | 340               |
| Piano/Keyboard      | 342               |
| Strings             | 2041              |
| Strings Analog      | 343               |
| Synth               | 2047              |
| Tuned Percussion    | 341               |
| Voice/Choir         | 346               |
| Woodwinds           | 2048              |

### Instruments in a sample library category

```
GET /wt/api/main/v1/sample_instruments_for_sample_library/{sample_library_id}/latest/
→ {
    "subcategories": [],
    "accordion_items": [
      {"id":1171,
       "title":"Harmonica Multi",
       "latest_version":"4.1",
       "file_size":"2.0 MB",
       "preview_file":"https://assets.nordkeyboards.com/…/Harmonica Multi.mp3",
       "download_url":"/wt/api/main/v1/download/sample_instruments_by_sound_version/8/1171/",
       "compatible_products":"/wt/api/main/v1/compatible_products/1171/",
       "subcategory":null},
      …
    ]
  }
```
The `download_url` contains a default keyboard code (e.g. 8). Substitute the connected
keyboard's API code before downloading.

### Individual sample instrument download

```
GET /wt/api/main/v1/download/sample_instruments_by_sound_version/{keyboardCode}/{instrumentId}/
→ application/octet-stream  (CBIN-formatted .nsmp file)
  Error 400: {"error":"Did not find any matching samples for product/sample-combination"}
  when keyboard is not compatible with that instrument.
```

### Entire category download (ZIP)

```
GET /wt/api/main/v1/download/sample_instruments/{keyboardCode}/library/{sample_library_id}/
→ ZIP archive of all instruments in the category for that keyboard
```

---

## Piano library products observed

| productCode | Name                      |
|-------------|---------------------------|
| 2279        | Astoria Grand             |
| 1788        | Soft Grand                |
| 329         | EP9 Stockholm             |
| 328         | Pearl Upright             |
| 1235        | Felt Upright              |
| 1270        | DX Rubba Tines            |
| 1240        | Mellow Upright            |
| 1259        | Electric Grand Amped      |
| 2288        | Supreme Horns             |
| 2353        | Lo-Fi Collection          |
| 2072        | Spitfire String Quintet   |
| 359         | Symphobia                 |
| 2047        | Synth                     |
| 340         | Organ                     |
(list is incomplete; 3 pages × 30 items = ~90 total products)
