#!/usr/bin/env python3
import struct

# Payload 1: Piano (Native) message
payload1_hex = "000000000c0000000e5069616e6f20284e6174697665290001ff00000000c000000002000000a40101010000000000010001000100000005506961"
payload1 = bytes.fromhex(payload1_hex)

# Payload 2: Synchronizing message
payload2 = bytes.fromhex("0000000000001053796e6368726f6e697a696e672e2e2e")

print("=" * 80)
print("PAYLOAD 1 ANALYSIS (Piano Native - truncated)")
print("=" * 80)
print(f"Hex: {payload1.hex()}\n")

pos = 0
print(f"[0:4]   = {payload1[0:4].hex()} = {struct.unpack('>I', payload1[0:4])[0]:10} (u32 big-endian)")
print(f"[4:8]   = {payload1[4:8].hex()} = {struct.unpack('>I', payload1[4:8])[0]:10} (u32 big-endian)")
print(f"[8:10]  = {payload1[8:10].hex()} = {struct.unpack('>H', payload1[8:10])[0]:10} (u16 big-endian)")
print(f"[10:12] = {payload1[10:12].hex()} (padding?)")
print(f"[12]    = {payload1[12]:02x} = {payload1[12]:3} (length byte)")
string_len = payload1[12]
print(f"[13:{13+string_len}] = '{payload1[13:13+string_len].decode('ascii', errors='ignore')}'")

print("\n" + "=" * 80)
print("PAYLOAD 2 ANALYSIS (Synchronizing)")
print("=" * 80)
print(f"Hex: {payload2.hex()}\n")

print(f"[0:4]   = {payload2[0:4].hex()} = {struct.unpack('>I', payload2[0:4])[0]:10} (u32)")
print(f"[4:8]   = {payload2[4:8].hex()} = {struct.unpack('>I', payload2[4:8])[0]:10} (u32)")
print(f"[8:10]  = {payload2[8:10].hex()} = {struct.unpack('>H', payload2[8:10])[0]:10} (u16)")
print(f"[10]    = {payload2[10]:02x} = {payload2[10]:3} (length byte)")
string_len = payload2[10]
print(f"[11:{11+string_len}] = '{payload2[11:11+string_len].decode('ascii', errors='ignore')}'")

print("\n" + "=" * 80)
print("PATTERN HYPOTHESIS")
print("=" * 80)
print("Payload structure appears to be:")
print("  [0:4]   - u32 (unknown, often 0x00000000)")
print("  [4:8]   - u32 (unknown, varies)")
print("  [8:10]  - u16 (unknown)")
print("  [10]    - u8 length of following string")
print("  [11:...] - ASCII string (length-prefixed)")
print("  [11+len:...] - More data/structures")
