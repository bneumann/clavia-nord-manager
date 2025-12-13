#!/usr/bin/env python3
"""
Nord Stage 3 USB Protocol Parser
Converts hex dump from Wireshark to binary and analyzes protocol structure
"""

import struct
import sys
from pathlib import Path

def crc16_ibm3740(data: bytes) -> int:
    """
    Calculate CRC-16/IBM-3740 (also known as CRC-16/CCITT-FALSE)
    Polynomial: 0x1021
    Initial value: 0xFFFF
    Final XOR: 0x0000
    Reflected input/output: False
    """
    crc = 0xFFFF
    poly = 0x1021
    
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc <<= 1
            if crc & 0x10000:
                crc ^= poly
            crc &= 0xFFFF
    
    return crc

class NordMessage:
    def __init__(self, data: bytes, is_host_to_device: bool):
        self.data = data
        self.is_host_to_device = is_host_to_device
        self.direction = "Host→Device" if is_host_to_device else "Device→Host"
        
    def parse(self):
        """Parse the message structure"""
        if len(self.data) < 16:
            return None
            
        # Parse header (all big-endian based on Wireshark display)
        length = struct.unpack('>I', self.data[0:4])[0]
        command = struct.unpack('>I', self.data[4:8])[0]
        param1 = struct.unpack('>I', self.data[8:12])[0]
        param2 = struct.unpack('>I', self.data[12:16])[0]
        checksum = int.from_bytes(self.data[-2:], byteorder='big', signed=False)
        
        payload = self.data[16:length-2]
        sound_type = self.getSoundType(payload)

        return {
            'length': length,
            'command': command,
            'param1': param1,
            'param2': param2,
            'payload': payload,
            'payload_hex': payload.hex(),
            'sound_type': sound_type,
            'checksum': checksum,
            'checksum_hex': hex(checksum),
            'total_size': len(self.data),
        }
    
    def extract_strings(self):
        """Extract ASCII strings from payload"""
        strings = []
        payload = self.data[16:-2]  # Skip header and checksum
        
        i = 0
        while i < len(payload) - 13:  # Need at least 13 bytes for a record
            # Check for the pattern: 5 zero bytes + 4-byte ID + 4-byte length
            if payload[i:i+5] == b'\x00\x00\x00\x00\x00':
                # Parse as StringRecord
                try:
                    id_val = struct.unpack('>I', payload[i+5:i+9])[0]
                    str_len = struct.unpack('>I', payload[i+9:i+13])[0]
                                   # Detect sound type from bytes at offset 17-20
 
                    # Validate: string length should be reasonable
                    if 0 < str_len < 256 and i + 13 + str_len <= len(payload):
                        string = payload[i+13:i+13+str_len].decode('ascii', errors='ignore')
                        if string.isprintable():             
                            
                            strings.append({
                                'offset': i,
                                'id': id_val,
                                'length': str_len,
                                'string': string
                            })
                            i += 13 + str_len
                            continue
                except:
                    pass
            i += 1
        
        return strings
    
    def getSoundType(self, payload):
        sound_type = None
        if len(payload) >= 19:
            sound_marker = payload[16:20].decode('ascii', errors='ignore')
            if sound_marker.isprintable():
                if sound_marker == 'npno':
                    sound_type = 'Piano'
                elif sound_marker == 'nsmp':
                    sound_type = 'Sample'
                elif sound_marker == 'ns3f':
                    sound_type = 'User?'
                elif sound_marker == 'ns3y':
                    sound_type = 'Synth'
                else:
                    sound_type = f'Other {sound_marker}'
        return sound_type

    def __str__(self):
        parsed = self.parse()
        if not parsed:
            return f"{self.direction}: {len(self.data)} bytes - TOO SHORT"
        
        result = f"{self.direction}: {len(self.data)} bytes\n"
        result += f"  Length: 0x{parsed['length']:08x}\n"
        result += f"  Command: 0x{parsed['command']:08x}\n"
        result += f"  Param1: 0x{parsed['param1']:08x}\n"
        result += f"  Param2: 0x{parsed['param2']:08x}\n"
        
        if parsed['payload']:
            result += f"  Payload ({len(parsed['payload'])} bytes): {parsed['payload_hex']}\n"
        
        if parsed['sound_type']:
            result += f"  Sound Type: {parsed['sound_type']}\n" if parsed['sound_type'] else ""
        
        strings = self.extract_strings()
        if strings:
            result += f"  Strings found:\n"
            for s in strings:
                
                result += f"    [{s['offset']}] ID=0x{s['id']:08x} Len={s['length']}: {s['string']}\n"
        
        if parsed['checksum']:
            result += f"  Checksum: {parsed['checksum_hex']}\n"
            verification = crc16_ibm3740(self.data[:-2]) == parsed['checksum']
            result += f"  Verification: {"VALID" if verification else "INVALID"}\n"

        return result


def parse_hex_file(filename: str):
    """Read hex dump file and convert to messages"""
    with open(filename, 'r') as f:
        lines = f.readlines()
    
    messages = []
    for line in lines:
        line = line.strip()
        if not line:
            continue
        
        # Convert hex string to bytes
        try:
            data = bytes.fromhex(line)
            messages.append(data)
        except ValueError as e:
            print(f"Error parsing line: {line[:50]}... - {e}", file=sys.stderr)
    
    return messages


def main():
    input_file = Path('tmp.txt')
    payload_file = Path('payloads.txt')
    output_bin = Path('nord_protocol.bin')
    
    if not input_file.exists():
        print(f"Error: {input_file} not found")
        sys.exit(1)
    
    print(f"Reading hex dump from {input_file}...")
    messages = parse_hex_file(str(input_file))
    print(f"Found {len(messages)} messages\n")
    
    # Write combined binary
    with open(output_bin, 'wb') as f:
        for msg in messages:
            f.write(msg)
    
    print(f"Wrote binary to {output_bin}\n")
    
    # Analyze each message
    print("=" * 80)
    print("PROTOCOL ANALYSIS")
    print("=" * 80)
    
    # Group by command type
    commands = {}
    
    for i, msg_data in enumerate(messages):
        # Assume alternating: odd = host→device, even = device→host
        is_host = (i % 2 == 0)
        msg = NordMessage(msg_data, is_host)
        
        parsed = msg.parse()
        if parsed:
            cmd = parsed['command']
            if cmd not in commands:
                commands[cmd] = []
            commands[cmd].append((i, msg))
            if parsed['payload']:
                with open(payload_file, 'a') as pf:
                    pf.write(f"{parsed['payload'].hex()}\n")
        
        print(f"Message {i}:")
        print(msg)
        print()
    
    # Summary by command
    print("\n" + "=" * 80)
    print("COMMAND SUMMARY")
    print("=" * 80)
    for cmd in sorted(commands.keys()):
        count = len(commands[cmd])
        print(f"Command 0x{cmd:08x}: {count} occurrences")
        # Show first occurrence
        idx, first_msg = commands[cmd][0]
        parsed = first_msg.parse()
        print(f"  First at message {idx}: {parsed['total_size']} bytes")


if __name__ == '__main__':
    main()
