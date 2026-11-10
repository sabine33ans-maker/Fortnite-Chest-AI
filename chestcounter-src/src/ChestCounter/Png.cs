using System.IO.Compression;

namespace ChestCounter;

/// <summary>Minimal PNG writer (no dependency), used to save overlay previews.</summary>
public static class Png
{
    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Writes top-down 32-bit BGRA pixels as an opaque RGB PNG.</summary>
    public static void Write(string path, int width, int height, byte[] bgra)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                raw.WriteByte(bgra[i + 2]);
                raw.WriteByte(bgra[i + 1]);
                raw.WriteByte(bgra[i]);
            }
        }
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zl);
        }

        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)width);
        BE(ihdr, 4, (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", z.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void BE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BE(head, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var c = 0xFFFFFFFFu;
        foreach (var b in head.AsSpan(4).ToArray().Concat(data)) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4];
        BE(crc, 0, c ^ 0xFFFFFFFF);
        s.Write(crc);
    }
}
