using System.Buffers.Binary;
using System.IO.Compression;
using ZetPDF.Core.Contracts;

namespace ZetPDF.Editing.Imaging;

/// <summary>Minimalny koder PNG (RGB 8 bit, filtr Up) – bez WPF, bezpieczny na dowolnym wątku.</summary>
internal static class PngWriter
{
    private static readonly uint[] Crc = BuildCrc();

    /// <summary>Bitmapa z renderera (BGRA premultiplied, tło nieprzezroczyste) → PNG RGB.</summary>
    public static byte[] FromBgra(RenderedBitmap b) => FromBgra(b.Bgra, b.Width, b.Height, b.Stride);

    public static byte[] FromBgra(byte[] bgra, int width, int height, int stride)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var prev = new byte[width * 3];
            var line = new byte[width * 3 + 1];
            for (int y = 0; y < height; y++)
            {
                line[0] = 2; // Up
                int s = y * stride;
                for (int x = 0; x < width; x++, s += 4)
                {
                    int a = bgra[s + 3];
                    byte r = (byte)Math.Min(255, bgra[s + 2] + 255 - a), g = (byte)Math.Min(255, bgra[s + 1] + 255 - a), bl = (byte)Math.Min(255, bgra[s] + 255 - a);
                    int o = x * 3;
                    line[1 + o] = (byte)(r - prev[o]); line[2 + o] = (byte)(g - prev[o + 1]); line[3 + o] = (byte)(bl - prev[o + 2]);
                    prev[o] = r; prev[o + 1] = g; prev[o + 2] = bl;
                }
                z.Write(line);
            }
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2; // 8 bit, RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in t) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(len, c ^ 0xFFFFFFFF);
        s.Write(len);
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
