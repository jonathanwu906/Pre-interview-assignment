using System.IO.Compression;

namespace GridAnalysis;

/// <summary>
/// Minimal zero-dependency PNG codec — System.Drawing is unavailable on
/// macOS/.NET, and pulling ImageSharp for one screenshot is overkill.
/// Decoder supports the formats screenshots actually use: 8-bit depth,
/// color types 0 (gray) / 2 (RGB) / 3 (palette) / 6 (RGBA), non-interlaced.
/// Encoder writes 8-bit RGB. zlib layer via System.IO.Compression.ZLibStream.
/// </summary>
public sealed class Png
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    /// <summary>Row-major RGB, 3 bytes per pixel.</summary>
    public byte[] Rgb { get; private init; } = Array.Empty<byte>();

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static Png Decode(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG file");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();

        int pos = 8;
        while (pos + 8 <= data.Length)
        {
            int len = ReadBe32(data, pos);
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int body = pos + 8;
            switch (type)
            {
                case "IHDR":
                    width = ReadBe32(data, body);
                    height = ReadBe32(data, body + 4);
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                    break;
                case "PLTE":
                    palette = data.AsSpan(body, len).ToArray();
                    break;
                case "IDAT":
                    idat.Write(data, body, len);
                    break;
                case "IEND":
                    pos = data.Length;
                    continue;
            }
            pos = body + len + 4; /* skip CRC */
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("missing IHDR");
        if (bitDepth != 8)
            throw new NotSupportedException($"bit depth {bitDepth} not supported (only 8)");
        if (interlace != 0)
            throw new NotSupportedException("interlaced PNG not supported");
        int bpp = colorType switch
        {
            0 => 1, 2 => 3, 3 => 1, 6 => 4,
            _ => throw new NotSupportedException($"color type {colorType} not supported"),
        };

        int stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int off = 0, n;
            while (off < raw.Length && (n = z.Read(raw, off, raw.Length - off)) > 0)
                off += n;
            if (off != raw.Length)
                throw new InvalidDataException("truncated image data");
        }

        Unfilter(raw, height, stride, bpp);

        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            int src = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                int d = (y * width + x) * 3;
                switch (colorType)
                {
                    case 0:
                        rgb[d] = rgb[d + 1] = rgb[d + 2] = raw[src + x];
                        break;
                    case 2:
                        rgb[d] = raw[src + x * 3];
                        rgb[d + 1] = raw[src + x * 3 + 1];
                        rgb[d + 2] = raw[src + x * 3 + 2];
                        break;
                    case 3:
                        if (palette == null)
                            throw new InvalidDataException("palette image without PLTE");
                        int pi = raw[src + x] * 3;
                        rgb[d] = palette[pi];
                        rgb[d + 1] = palette[pi + 1];
                        rgb[d + 2] = palette[pi + 2];
                        break;
                    case 6:
                        rgb[d] = raw[src + x * 4];
                        rgb[d + 1] = raw[src + x * 4 + 1];
                        rgb[d + 2] = raw[src + x * 4 + 2];
                        break;
                }
            }
        }
        return new Png { Width = width, Height = height, Rgb = rgb };
    }

    /* PNG filters reconstruct each byte from left (a), up (b), up-left (c). */
    private static void Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int line = y * (stride + 1);
            byte filter = raw[line];
            int cur = line + 1;
            int prev = cur - (stride + 1);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? raw[cur + i - bpp] : 0;
                int b = y > 0 ? raw[prev + i] : 0;
                int c = y > 0 && i >= bpp ? raw[prev + i - bpp] : 0;
                int add = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"bad filter {filter}"),
                };
                raw[cur + i] = (byte)(raw[cur + i] + add);
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>rowFilters selects the PNG filter per scanline (default: all 0);
    /// non-zero filters exist so the decoder's unfilter paths are testable.</summary>
    public static void EncodeRgb(string path, int width, int height, byte[] rgb, byte[]? rowFilters = null)
    {
        int stride = width * 3;
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            var buf = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                byte f = rowFilters == null ? (byte)0 : rowFilters[y];
                int off = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 3 ? rgb[off + i - 3] : 0;
                    int b = y > 0 ? rgb[off - stride + i] : 0;
                    int c = y > 0 && i >= 3 ? rgb[off - stride + i - 3] : 0;
                    int pred = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                    buf[i] = (byte)(rgb[off + i] - pred);
                }
                z.WriteByte(f);
                z.Write(buf, 0, stride);
            }
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(Signature, 0, Signature.Length);
        var ihdr = new byte[13];
        WriteBe32(ihdr, 0, width);
        WriteBe32(ihdr, 4, height);
        ihdr[8] = 8;  /* bit depth */
        ihdr[9] = 2;  /* RGB */
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", idat.ToArray());
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] body)
    {
        var head = new byte[8];
        WriteBe32(head, 0, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head, 0, 8);
        s.Write(body, 0, body.Length);
        uint crc = Crc32(head.AsSpan(4, 4), body);
        var tail = new byte[4];
        WriteBe32(tail, 0, (int)crc);
        s.Write(tail, 0, 4);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> head, ReadOnlySpan<byte> body)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in head)
            c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in body)
            c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static int ReadBe32(byte[] d, int off) =>
        (d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3];

    private static void WriteBe32(byte[] d, int off, int v)
    {
        d[off] = (byte)(v >> 24);
        d[off + 1] = (byte)(v >> 16);
        d[off + 2] = (byte)(v >> 8);
        d[off + 3] = (byte)v;
    }
}
