import usb.core
import usb.util

# Find all connected USB devices
devices = usb.core.find(find_all=True)

# Print information about each device
for device in devices:
    print(f"Device: {device.idVendor}:{device.idProduct}")

# Bus 001 Device 008: ID 0ffc:0026 Clavia DMI AB Nord Stage 3



# Check endpoint capabilities:
device = usb.core.find(idVendor=0x0ffc, idProduct=0x0026)
for cfg in device:
    for intf in cfg:
        for ep in intf:
            print(f"Endpoint: {ep.bEndpointAddress:02x}, Type: {ep.bmAttributes}, MaxPacket: {ep.wMaxPacketSize}")


# Read string descriptor index 3 (device name)
try:
    string_descriptor = device.ctrl_transfer(
        bmRequestType=0x80,  # Device to host, standard
        bRequest=0x06,       # GET_DESCRIPTOR
        wValue=0x0303,       # String descriptor index 3
        wIndex=0x0409,       # Language: English US
        data_or_wLength=26
    )
    text = bytes(string_descriptor[2:]).decode('utf-16-le', errors='ignore')
    print(f"Device name: {text}")
except Exception as e:
    print(f"Error reading descriptor: {e}")

