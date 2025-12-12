#pragma description("Nord Stage 3 USB Protocol")
#pragma endian big

// Helper function to extract null-terminated or length-prefixed strings
fn get_string(u32 max_len) {
    u8 str_data[max_len];
    return str_data;
};

// Length-prefixed string structure
struct LengthString {
    u8 length;
    char data[length];
};

// Main Nord USB Message Format
struct NordMessage {
    u32 length;          // Total message length (0x00000012 = 18 bytes)
    u32 command;         // Command code (0x00000007, 0x0000000c, 0x0000000a, etc)
    u32 param1;          // Parameter 1 (query ID, request type)
    u32 param2;          // Parameter 2 (sequence number, data ID)
    
    // Payload: everything between header and checksum
    u8 payload[length - 18];
    
    u16 checksum;        // CRC or checksum (last 2 bytes)
};

// Parse all messages in the file as an array
// Since we don't know the exact count, we estimate based on typical message sizes
NordMessage messages[5189] @ 0x00;  // From the parser output: 5189 messages total