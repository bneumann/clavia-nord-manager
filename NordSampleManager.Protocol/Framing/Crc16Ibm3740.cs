namespace NordSampleManager.Protocol.Framing;

public static class Crc16Ibm3740
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        const ushort polynomial = 0x1021;
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ polynomial);
                else
                    crc <<= 1;
            }
        }
        return crc;
    }
}
