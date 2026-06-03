# Clavia Nord Reverse Engineering

This repo reverse-engineers the Clavia Nord Stage 3 USB protocol and builds a Linux-native .NET desktop manager (`NordSampleManager/`, Avalonia) to replace the Windows-only Nord Sound Manager. Python scripts (`nord_api.py`, `parse_nord_protocol.py`, `interpret_protocol.py`) remain the RE sandbox; confirmed protocol behavior is ported into the C# `NordSampleManager.Protocol` library.

## Quickstart (Linux)

```bash
# 1) Grant the active user permission to talk to the keyboard
sudo cp 99-nord.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
# re-plug the Nord

# 2) Build and launch the desktop app
dotnet build NordSampleManager.sln
dotnet run --project NordSampleManager
```

Click "Connect & load". If `LIBUSB_ERROR_ACCESS` shows up in the status bar, the udev rule isn't installed or the device wasn't re-plugged.

---

## Protocol reference

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

## Protokoll GET
Command: 0x0000000c
Param1: 0x0000000a

### Piano Categories:
Param2: 0x00000002
Payload: 00000001

Returns:
Grand, Upright, Electric, Clav, ...

### Bank:
Param2: 0x00000002
Payload: 00000002, 00000003, 00000004, 00000006

Returns: 
Name, and a number ?

Remark for Payloads: 
- 2,3,4 and 6 return "Bank 1" but 5 returns "Samp Lib"
- 7 returns Banks A to P
- 8 returns Bank 1 to 8
- 9 returns Bank 1 to 8

### SampLib
Param2: 0x00000002
Payload: 00000005

Returns: 
Name, and a number ?

### Banks A-P
Param2: 0x00000002
Payload: 00000007

Returns: 
Banks A to P and a trailing number

### Banks 1-8 (1.)
Param2: 0x00000002
Payload: 00000008

Returns: 
Banks 1 to 8 and a trailing number

### Banks 1-8 (2.)
Param2: 0x00000002
Payload: 00000009

Returns: 
Banks 1 to 8 and a trailing number

## Program
Param2: 0x00000028
Payload: 
00000000 Query Program
000000NN (Program Number)

Returns:

## Songs
Param2: 0x00000028
Payload:
00000001 Query Song
000000NN Song Number


## Pianos
Param2: 0x0000001e
Payload:
000000nn Piano Categories
000000nn Location (Number in Category)

Returns:
2nd word: Piano Categorie
3rd word: Location (Number in Category)
4th word: Size in bytes 0c4e64f8 -> 197MB
5th word: Identifier "npno"
6th word: Version*100 -> 6.30 = 630 = 276
9th word: Length of Description and Version
10th word and following: Description and Version as string