#!/usr/bin/env python3
"""
Nord Protocol Binary Interpreter
Reads nord_protocol.bin and displays messages in a human-readable format
"""

import struct
import sys
from pathlib import Path
from typing import List, Dict, Optional

def crc16_ibm3740(data: bytes) -> int:
    """Calculate CRC-16/IBM-3740"""
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
    """Represents a single Nord protocol message"""
    
    COMMAND_NAMES = {
        0x00000007: 'INIT',
        0x00000006: 'STATUS',
        0x0000000c: 'QUERY',
        0x0000000a: 'PARAM_QUERY',
    }
    
    QUERY_TYPES = {
        0x00000002: 'PIANO_CATEGORIES',
        0x00000028: 'PROGRAM/SONG',
        0x0000001e: 'PIANO_DETAIL',
    }
    
    def __init__(self, data: bytes, offset: int = 0):
        """Parse a message from binary data"""
        self.offset = offset
        self.data = data
        self.valid = False
        
        if len(data) < 18:  # Minimum: 16 bytes header + 2 bytes checksum
            return
        
        try:
            # Parse header
            self.length = struct.unpack('>I', data[0:4])[0]
            self.command = struct.unpack('>I', data[4:8])[0]
            self.param1 = struct.unpack('>I', data[8:12])[0]
            self.param2 = struct.unpack('>I', data[12:16])[0]
            
            # Validate length
            if self.length > len(data) or self.length < 18:
                return
            
            # Extract payload and checksum
            self.payload = data[16:self.length-2]
            self.checksum = struct.unpack('>H', data[self.length-2:self.length])[0]
            
            # Verify CRC
            self.calculated_crc = crc16_ibm3740(data[:self.length-2])
            self.crc_valid = self.calculated_crc == self.checksum
            
            self.valid = True
            self.direction = self._determine_direction()
            
        except Exception as e:
            pass
    
    def _determine_direction(self) -> str:
        """Guess if this is host→device or device→host based on structure"""
        # Device responses often have longer payloads with data
        if len(self.payload) > 50:
            return "Device→Host"
        return "Host→Device"
    
    def get_command_name(self) -> str:
        """Get human-readable command name"""
        return self.COMMAND_NAMES.get(self.command, f"0x{self.command:08x}")
    
    def get_query_type(self) -> Optional[str]:
        """Get query type if this is a QUERY command"""
        if self.command == 0x0000000c and self.param1 == 0x0000000a:
            return self.QUERY_TYPES.get(self.param2, f"Type 0x{self.param2:08x}")
        return None
    
    def extract_strings(self) -> List[Dict]:
        """Extract strings from payload"""
        strings = []
        payload = self.payload
        
        i = 0
        while i < len(payload) - 13:
            # Look for the pattern: 5 zero bytes + 4-byte ID + 4-byte length
            if payload[i:i+5] == b'\x00\x00\x00\x00\x00':
                try:
                    id_val = struct.unpack('>I', payload[i+5:i+9])[0]
                    str_len = struct.unpack('>I', payload[i+9:i+13])[0]
                    
                    if 0 < str_len < 256 and i + 13 + str_len <= len(payload):
                        string = payload[i+13:i+13+str_len].decode('ascii', errors='ignore')
                        if string.isprintable():
                            strings.append({
                                'offset': i,
                                'id': id_val,
                                'length': str_len,
                                'string': string,
                            })
                            i += 13 + str_len
                            continue
                except:
                    pass
            i += 1
        
        return strings
    
    def __str__(self) -> str:
        """Format message for display"""
        if not self.valid:
            return f"[Invalid message at offset 0x{self.offset:08x}]"
        
        result = f"\n{'='*80}\n"
        result += f"Message at offset: 0x{self.offset:08x}\n"
        result += f"Direction: {self.direction}\n"
        result += f"Total size: {self.length} bytes\n"
        result += f"\nHeader:\n"
        result += f"  Command: {self.get_command_name()} (0x{self.command:08x})\n"
        result += f"  Param1:  0x{self.param1:08x}\n"
        result += f"  Param2:  0x{self.param2:08x}\n"
        
        query_type = self.get_query_type()
        if query_type:
            result += f"  Query Type: {query_type}\n"
        
        if self.payload:
            result += f"\nPayload ({len(self.payload)} bytes):\n"
            # Show hex dump
            hex_str = self.payload.hex()
            for i in range(0, len(hex_str), 32):
                result += f"  {hex_str[i:i+32]}\n"
            
            # Extract and show strings if found
            strings = self.extract_strings()
            if strings:
                result += f"\nStrings found ({len(strings)}):\n"
                for s in strings:
                    result += f"  [{s['offset']:3d}] ID=0x{s['id']:08x} Len={s['length']:2d}: {s['string']}\n"
        
        result += f"\nChecksum: 0x{self.checksum:04x} (Calculated: 0x{self.calculated_crc:04x})"
        if self.crc_valid:
            result += " ✓ VALID\n"
        else:
            result += " ✗ INVALID\n"
        
        return result


def interpret_binary(binary_file: Path, max_messages: Optional[int] = None) -> None:
    """Interpret and display messages from binary file"""
    
    with open(binary_file, 'rb') as f:
        data = f.read()
    
    print(f"Reading {len(data)} bytes from {binary_file}\n")
    
    offset = 0
    msg_count = 0
    valid_count = 0
    
    while offset < len(data):
        msg = NordMessage(data[offset:], offset)
        
        if msg.valid:
            valid_count += 1
            if max_messages is None or msg_count < max_messages:
                print(msg)
            msg_count += 1
            offset += msg.length
        else:
            offset += 1
    
    print(f"\n{'='*80}")
    print(f"Summary:")
    print(f"  Total bytes: {len(data)}")
    print(f"  Valid messages found: {valid_count}")
    print(f"  Messages displayed: {min(msg_count, max_messages or msg_count)}")


def main():
    """Main entry point"""
    binary_file = Path('nord_protocol.bin')
    
    if not binary_file.exists():
        print(f"Error: {binary_file} not found")
        sys.exit(1)
    
    # Show first 20 messages by default
    max_messages = 20
    
    if len(sys.argv) > 1:
        try:
            max_messages = int(sys.argv[1])
        except ValueError:
            print(f"Usage: {sys.argv[0]} [max_messages]")
            sys.exit(1)
    
    interpret_binary(binary_file, max_messages)


if __name__ == '__main__':
    main()
