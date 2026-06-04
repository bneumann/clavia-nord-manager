#!/usr/bin/env python3
"""
Nord Stage 3 USB Protocol API
Implements the reverse-engineered protocol for querying device data
"""

import struct
import usb.core
import usb.util
from typing import List, Dict, Optional

class NordProtocol:
    """Nord Stage 3 USB Protocol Implementation"""
    
    # Command codes
    CMD_INIT = 0x00000007
    CMD_QUERY = 0x0000000c
    
    # Query parameter (seems to be standard for queries)
    PARAM_QUERY = 0x0000000a
    
    # Query types (Param2 values)
    QUERY_PIANO_CATEGORIES = 0x00000002  # Returns Grand, Upright, Electric, etc.
    QUERY_BANK_1TO4_6 = 0x00000002      # Various bank queries
    QUERY_SAMPLIB = 0x00000002
    QUERY_BANKS_AP = 0x00000002
    QUERY_BANKS_18_V1 = 0x00000002
    QUERY_BANKS_18_V2 = 0x00000002
    QUERY_PROGRAM = 0x00000028
    QUERY_SONG = 0x00000028
    QUERY_PIANO = 0x0000001e
    
    # Payload type identifiers
    PAYLOAD_PIANO_CATEGORIES = 0x00000001
    PAYLOAD_BANK_1 = 0x00000002
    PAYLOAD_BANK_2 = 0x00000003
    PAYLOAD_BANK_3 = 0x00000004
    PAYLOAD_SAMPLIB = 0x00000005
    PAYLOAD_BANK_4 = 0x00000006
    PAYLOAD_BANKS_AP = 0x00000007
    PAYLOAD_BANKS_18_V1 = 0x00000008
    PAYLOAD_BANKS_18_V2 = 0x00000009
    
    def __init__(self, vendor_id=0x0ffc, product_id=0x0026):
        """Initialize connection to Nord Stage 3"""
        self.device = usb.core.find(idVendor=vendor_id, idProduct=product_id)
        
        if self.device is None:
            raise RuntimeError("Nord Stage 3 not found")
        
        # Detach kernel driver if needed
        if self.device.is_kernel_driver_active(0):
            self.device.detach_kernel_driver(0)
        
        # Find the correct endpoints (0x03 OUT, 0x82 IN for bulk transfers)
        intf = self.device[0][(0, 0)]
        
        # Find bulk OUT endpoint
        self.ep_out = None
        for ep in intf:
            if usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_OUT and \
               (usb.util.endpoint_type(ep.bmAttributes)) == usb.util.ENDPOINT_TYPE_BULK:
                self.ep_out = ep
                break
        
        # Find bulk IN endpoint
        self.ep_in = None
        for ep in intf:
            if usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_IN and \
               (usb.util.endpoint_type(ep.bmAttributes)) == usb.util.ENDPOINT_TYPE_BULK:
                self.ep_in = ep
                break
        
        if self.ep_out is None or self.ep_in is None:
            raise RuntimeError("Could not find bulk endpoints")
        
        print(f"Found endpoints: OUT=0x{self.ep_out.bEndpointAddress:02x}, IN=0x{self.ep_in.bEndpointAddress:02x}")
    
    def _build_message(self, command: int, param1: int, param2: int, payload: bytes = b'') -> bytes:
        """Build a Nord protocol message"""
        length = 16 + len(payload) + 2  # header + payload + checksum
        
        msg = struct.pack('>IIII', length, command, param1, param2)
        msg += payload
        
        # Calculate CRC-16/IBM-3740
        crc = self._crc16_ibm3740(msg)
        msg += struct.pack('>H', crc)
        
        return msg
    
    def _crc16_ibm3740(self, data: bytes) -> int:
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
    
    def query_piano_categories(self) -> List[str]:
        """Query piano categories (Grand, Upright, Electric, etc.)"""
        payload = struct.pack('>I', self.PAYLOAD_PIANO_CATEGORIES)
        msg = self._build_message(self.CMD_QUERY, self.PARAM_QUERY, 0x00000002, payload)
        print(f"Sending message to EP 0x{self.ep_out.bEndpointAddress:02x}: {msg.hex()}")
        print(f"  Length: {len(msg)} bytes, Command: 0x{self.CMD_QUERY:08x}")
        self.device.write(self.ep_out.bEndpointAddress, msg)
        response = self.device.read(self.ep_in.bEndpointAddress, 512)
        print(f"Received response from EP 0x{self.ep_in.bEndpointAddress:02x}: {bytes(response).hex()[:80]}...")
        
        return self._parse_string_list(bytes(response))
    
    def query_bank(self, bank_num: int) -> Optional[str]:
        """Query a specific bank name"""
        # Determine payload based on bank number
        if bank_num in [1, 2, 3, 4, 6]:
            payload_type = [0x00000002, 0x00000003, 0x00000004, 0x00000006][bank_num - 1]
        else:
            return None
        
        payload = struct.pack('>I', payload_type)
        msg = self._build_message(self.CMD_QUERY, self.PARAM_QUERY, 0x00000002, payload)
        print(f"Querying Bank {bank_num} to EP 0x{self.ep_out.bEndpointAddress:02x}: {msg.hex()[:40]}...")
        
        self.device.write(self.ep_out.bEndpointAddress, msg)
        response = self.device.read(self.ep_in.bEndpointAddress, 512)
        print(f"Response from EP 0x{self.ep_in.bEndpointAddress:02x}: {bytes(response).hex()[:40]}...")
        
        return self._parse_string_list(bytes(response))
    
    def query_samplib(self) -> Optional[str]:
        """Query sample library"""
        payload = struct.pack('>I', self.PAYLOAD_SAMPLIB)
        msg = self._build_message(self.CMD_QUERY, self.PARAM_QUERY, 0x00000002, payload)
        print(f"Querying SampLib to EP 0x{self.ep_out.bEndpointAddress:02x}: {msg.hex()[:40]}...")
        
        self.device.write(self.ep_out.bEndpointAddress, msg)
        response = self.device.read(self.ep_in.bEndpointAddress, 512)
        print(f"Response from EP 0x{self.ep_in.bEndpointAddress:02x}: {bytes(response).hex()[:40]}...")
        
        return self._parse_string_list(bytes(response))
    
    def _parse_string_list(self, data: bytes) -> List[str]:
        """Parse the string list response format"""
        # Skip initial bytes and extract strings
        strings = []
        
        # Skip header (varies)
        offset = 0
        
        # Look for string length byte and extract strings
        while offset < len(data) - 2:
            length = data[offset]
            if 0 < length < 128 and offset + length + 1 <= len(data):
                try:
                    string = data[offset+1:offset+1+length].decode('ascii', errors='ignore')
                    if string.isprintable():
                        strings.append(string)
                        offset += length + 1
                        continue
                except:
                    pass
            offset += 1
        
        return strings
    
    def close(self):
        """Close device connection"""
        if self.device:
            usb.util.dispose_resources(self.device)


def main():
    """Example usage"""
    try:
        nord = NordProtocol()
        
        print("Nord Stage 3 Protocol API Test\n")
        
        # Query piano categories
        print("Piano Categories:")
        categories = nord.query_piano_categories()
        for cat in categories:
            print(f"  - {cat}")
        
        nord.close()
    except Exception as e:
        print(f"Error: {e}")


if __name__ == '__main__':
    main()
