#pragma description("Nord Stage 3 USB Protocol")
#pragma endian big

// Helper function to extract null-terminated or length-prefixed strings
fn get_string(u32 max_len) {
    u8 str_data[max_len];
    return str_data;
};

// String record in payload
struct StringRecord {
    u8 padding[5];       // 5 zero bytes
    u32 id_or_type;      // ID or type identifier
    u32 str_length;      // String length (big-endian)
    char string[str_length];  // The actual string
};

// Main Nord USB Message Format
struct NordMessage {
    u32 length;          // Total message length
    u32 command;         // Command code (0x00000007, 0x0000000c, etc)
    u32 param1;          // Parameter 1 (query ID, request type)
    u32 param2;          // Parameter 2 (sequence number, data ID)
    
    // Payload: everything between header and checksum
    u8 payload[length - 18];
    
    u16 checksum;        // CRC or checksum (last 2 bytes)
};

// Parse all messages in the file as an array
NordMessage messages[5189] @ 0x00;  // From the parser output: 5189 messages total