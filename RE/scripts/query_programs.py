#!/usr/bin/env python3
"""
Phase 1 verification: query individual program metadata from Nord Stage 3.

Protocol findings (RE agent, 2026-06-03):
  - Query:    CMD=0x0c  Param1=0x0a  Param2=0x21  payload(12) = 00000000 | container | item
  - Response: Param2=0x1f  payload layout:
        [8-11]   echoed index (u32 BE)
        [12-15]  file size on-disk
        [16-19]  file type: "ns3f"=Program, "npno"=Piano, "nsmp"=Sample,
                             "ns3y"=Synth, "ns3s"=Song, "ns3l"=Live, "npdl"=PedalNoise
        [20-23]  version * 100 (u32 BE)
        [28-31]  category field
        [32-35]  name length (u32 BE)
        [36+]    name (ASCII)
  - End-of-container marker: device returns Param2=0x20 with ffffffff in bytes [4:8]
  - Programs (ns3f): containers 0-13 (=Banks A-N), up to 25 items each

Run:
    source .venv/bin/activate
    python query_programs.py
"""

import struct
import sys
import usb.core
import usb.util

VENDOR_ID  = 0x0ffc
PRODUCT_ID = 0x0026

CMD_INIT  = 0x00000007
CMD_QUERY = 0x0000000c
PARAM_QUERY = 0x0000000a

PARAM2_ITEM_QUERY    = 0x00000021   # host→device: request item (container, item_index)
PARAM2_ITEM_ECHO     = 0x0000001e   # device→host: echo of requested index
PARAM2_ITEM_DATA     = 0x0000001f   # device→host: item metadata
PARAM2_END_MARKER    = 0x00000020   # device→host: end-of-container / empty slot
PARAM2_LIST_BANKS    = 0x00000002   # confirmed-working bank-name list

SELECTOR_BANKS_A_TO_P = 0x00000007

# Program (ns3f) containers: 0=BankA, 1=BankB, …, 13=BankN
PROGRAM_CONTAINERS = 14
PROGRAM_ITEMS_PER_CONTAINER = 25


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) if crc & 0x10000 else (crc << 1)
            crc &= 0xFFFF
    return crc


def build_msg(param2: int, payload: bytes) -> bytes:
    length = 16 + len(payload) + 2
    header = struct.pack('>IIII', length, CMD_QUERY, PARAM_QUERY, param2)
    body = header + payload
    return body + struct.pack('>H', crc16(body))


def build_init() -> bytes:
    length = 18
    header = struct.pack('>IIII', length, CMD_INIT, 0, 2)
    return header + struct.pack('>H', crc16(header))


def connect():
    dev = usb.core.find(idVendor=VENDOR_ID, idProduct=PRODUCT_ID)
    if dev is None:
        sys.exit("Nord Stage 3 not found. Is it plugged in and do udev rules grant access?")
    if dev.is_kernel_driver_active(0):
        dev.detach_kernel_driver(0)
    intf = dev[0][(0, 0)]
    ep_out = next(ep for ep in intf
                  if usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_OUT
                  and usb.util.endpoint_type(ep.bmAttributes) == usb.util.ENDPOINT_TYPE_BULK)
    ep_in  = next(ep for ep in intf
                  if usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_IN
                  and usb.util.endpoint_type(ep.bmAttributes) == usb.util.ENDPOINT_TYPE_BULK)
    print(f"Connected  OUT=0x{ep_out.bEndpointAddress:02x}  IN=0x{ep_in.bEndpointAddress:02x}")
    return dev, ep_out, ep_in


def send_recv(dev, ep_out, ep_in, param2: int, payload: bytes, buf_size=4096) -> bytes:
    msg = build_msg(param2, payload)
    dev.write(ep_out.bEndpointAddress, msg)
    return bytes(dev.read(ep_in.bEndpointAddress, buf_size))


def parse_string_list(data: bytes):
    strings, offset = [], 0
    while offset < len(data) - 1:
        n = data[offset]
        if 0 < n < 128 and offset + 1 + n <= len(data):
            try:
                s = data[offset+1:offset+1+n].decode('ascii', errors='strict')
                if s.isprintable():
                    strings.append(s)
                    offset += 1 + n
                    continue
            except UnicodeDecodeError:
                pass
        offset += 1
    return strings


def parse_response(raw: bytes):
    """
    Unpack a raw USB response into (param2, msg_payload).
    Returns (None, b'') if the message is too short.
    """
    if len(raw) < 18:
        return None, b''
    msg_len   = struct.unpack('>I', raw[0:4])[0]
    param2    = struct.unpack('>I', raw[12:16])[0]
    payload   = raw[16:msg_len-2] if msg_len > 18 else b''
    return param2, payload


def is_end_marker(param2: int, payload: bytes) -> bool:
    """Param2=0x20 with ffffffff at bytes [4:8] = end-of-container."""
    if param2 != PARAM2_END_MARKER:
        return False
    if len(payload) >= 8 and payload[4:8] == b'\xff\xff\xff\xff':
        return True
    return False


def decode_1f(payload: bytes):
    """
    Decode a Param2=0x1f item-metadata payload.
    Returns (name, file_type, version_raw, category_field) or None.
    """
    if len(payload) < 40:
        return None
    file_type   = payload[16:20].decode('ascii', errors='replace')
    version_raw = struct.unpack('>I', payload[20:24])[0]
    category    = struct.unpack('>I', payload[28:32])[0]
    name_len    = struct.unpack('>I', payload[32:36])[0]
    if name_len == 0 or 36 + name_len > len(payload):
        return None
    name = payload[36:36+name_len].decode('ascii', errors='replace')
    return name, file_type, version_raw, category


def query_item(dev, ep_out, ep_in, container: int, item: int):
    """
    Send Param2=0x21 for (container, item).
    Returns decoded (name, file_type, version_raw, category) or None.
    Prints raw response on error.
    """
    payload = struct.pack('>III', 0, container, item)
    raw = send_recv(dev, ep_out, ep_in, PARAM2_ITEM_QUERY, payload)
    resp_param2, resp_payload = parse_response(raw)

    if resp_param2 is None:
        print(f"    [c={container} i={item}] too-short response: {raw.hex()}")
        return None

    # Device may first send 0x1e (echo) and we need to read again for 0x1f
    if resp_param2 == PARAM2_ITEM_ECHO:
        raw2 = bytes(dev.read(ep_in.bEndpointAddress, 4096))
        resp_param2, resp_payload = parse_response(raw2)

    if is_end_marker(resp_param2, resp_payload):
        return None   # empty slot or end of container

    if resp_param2 == PARAM2_ITEM_DATA:
        return decode_1f(resp_payload)

    print(f"    [c={container} i={item}] unexpected Param2=0x{resp_param2:08x}  payload={resp_payload.hex()}")
    return None


def main():
    dev, ep_out, ep_in = connect()

    # CMD_INIT handshake
    init_msg = build_init()
    dev.write(ep_out.bEndpointAddress, init_msg)
    dev.read(ep_in.bEndpointAddress, 512)
    print("INIT done\n")

    # ── Sanity: confirmed bank list ────────────────────────────────────────────
    raw = send_recv(dev, ep_out, ep_in, PARAM2_LIST_BANKS,
                    struct.pack('>I', SELECTOR_BANKS_A_TO_P))
    banks = parse_string_list(raw)
    print(f"Bank list ({len(banks)} banks): {banks}\n")

    # ── Phase-1 hypothesis: Param2=0x21 (container, item) ─────────────────────
    print("── Testing Param2=0x21 for first 3 programs ──")
    expected = {
        (0, 0): "Royal Grand 3D",
        (0, 1): "Hybrid Super MW",
        (0, 2): "B3 Jazzy Joey",
    }
    all_ok = True
    for (c, i), exp_name in expected.items():
        result = query_item(dev, ep_out, ep_in, c, i)
        if result is None:
            print(f"  (c={c}, i={i})  → NO RESPONSE  [expected {exp_name!r}]")
            all_ok = False
        else:
            name, ftype, ver_raw, cat = result
            ver_str = f"{ver_raw // 100}.{ver_raw % 100:02d}"
            match = "✓" if name == exp_name else "✗"
            print(f"  (c={c}, i={i})  name={name!r}  type={ftype}  v={ver_str}  cat=0x{cat:08x}  {match}")
            if name != exp_name:
                print(f"           expected: {exp_name!r}")
                all_ok = False

    print()
    if not all_ok:
        print("Phase 1 FAIL — trying Param2=0x1e fallback (old hypothesis) for index 0...")
        fallback_payload = struct.pack('>II', 0, 0)
        raw = send_recv(dev, ep_out, ep_in, 0x0000001e, fallback_payload)
        rp, rpl = parse_response(raw)
        print(f"  0x1e fallback: response param2=0x{rp:08x}  payload={rpl.hex()}")
        result = decode_1f(rpl) if rp == PARAM2_ITEM_DATA else None
        if result:
            print(f"  0x1e decoded: {result}")
        usb.util.dispose_resources(dev)
        return

    # ── Phase-1 full scan: all program containers ──────────────────────────────
    print("Phase 1 PASS — scanning all program containers (ns3f)...\n")
    programs = []
    for container in range(PROGRAM_CONTAINERS):
        bank_letter = chr(ord('A') + container)
        for item in range(PROGRAM_ITEMS_PER_CONTAINER):
            result = query_item(dev, ep_out, ep_in, container, item)
            if result is None:
                break  # end of this container
            name, ftype, ver_raw, cat = result
            if ftype == 'ns3f':
                row = item // 5 + 1
                col = item % 5 + 1
                location = row * 10 + col
                ver_str = f"{ver_raw // 100}.{ver_raw % 100:02d}"
                programs.append((f"Bank {bank_letter}", location, name, ver_str, cat))

    print(f"Programs found: {len(programs)}")
    print(f"{'Bank':<8} {'Loc':>4}  {'Name':<35} {'Ver':>5}  Cat")
    print("-" * 70)
    for bank, loc, name, ver, cat in programs[:40]:
        print(f"{bank:<8} {loc:>4}  {name:<35} {ver:>5}  0x{cat:08x}")
    if len(programs) > 40:
        print(f"  … {len(programs)-40} more …")

    usb.util.dispose_resources(dev)


if __name__ == '__main__':
    main()
