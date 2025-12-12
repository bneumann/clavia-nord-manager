# Clavia Nord Reverse Engineering
Endpoint addresses:

0x81 = Endpoint 1 IN (device to host), Type 3 = Interrupt
0x82 = Endpoint 2 IN (device to host), Type 2 = Bulk
0x03 = Endpoint 3 OUT (host to device), Type 2 = Bulk
0x04 = Endpoint 4 OUT (host to device), Type 2 = Bulk
0x84 = Endpoint 4 IN (device to host), Type 2 = Bulk
What you see in Wireshark:

1.3.2 (Bulk IN): Device sending you library/program data, metadata, or file listings
1.3.3 (Bulk OUT): Host sending commands like "get program list", "get sample list", "upload program", etc.

Likely usage for a Nord Stage:
0x81 (Interrupt IN): Real-time MIDI or control data
0x02 (Bulk OUT): Host sending commands/audio
0x03 (Bulk OUT): Host sending configuration
0x04 (Bulk OUT): Host sending more data
0x84 (Bulk IN): Device sending audio/data back


# Get bulk data from capture:
tshark -r /home/benni/git/nord-manager-re/detection+readlibrary.pcapng -Y 'usb.src == "1.3.2" || usb.dst == "1.3.3"' -T fields -e usb.capdata > tmp.txt