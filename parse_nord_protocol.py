#!/usr/bin/env python3
"""
Nord Stage 3 USB Protocol Parser
Converts hex dump from Wireshark to binary and analyzes protocol structure
"""

import struct
import sys
from pathlib import Path

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
        
        payload = self.data[16:]
        
        return {
            'length': length,
            'command': command,
            'param1': param1,
            'param2': param2,
            'payload': payload,
            'payload_hex': payload.hex(),
            'checksum': payload[-2:].hex() if len(payload) >= 2 else None,
            'total_size': len(self.data),
        }
    
    def extract_strings(self):
        """Extract ASCII strings from payload"""
        strings = []
        payload = self.data[16:]
        
        i = 0
        while i < len(payload) - 2:
            # Look for length-prefixed strings
            length = payload[i]
            if 0 < length < 128 and i + length + 1 <= len(payload):
                try:
                    string = payload[i+1:i+1+length].decode('ascii', errors='ignore')
                    if string.isprintable() and len(string) > 2:
                        strings.append((i, length, string))
                        i += length + 1
                        continue
                except:
                    pass
            i += 1
        
        return strings
    
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
            result += f"  Payload ({len(parsed['payload'])} bytes): {parsed['payload_hex'][:80]}...\n"
        
        strings = self.extract_strings()
        if strings:
            result += f"  Strings found:\n"
            for offset, length, string in strings:
                result += f"    [{offset}] ({length}): {string}\n"
        
        if parsed['checksum']:
            result += f"  Checksum: {parsed['checksum']}\n"
        
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
    input_file = Path('/home/benni/git/nord-manager-re/tmp.txt')
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
