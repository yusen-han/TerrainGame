using System;
using System.IO;
using System.IO.Compression;

namespace TerrainGame.Graphics;

/// <summary>
/// Minimal PNG encoder for BGRA8 pixel buffers. Used to hand the off-screen
/// frame to MAUI's <see cref="Microsoft.Maui.Controls.Image"/> via a stream.
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];

        for (var y = 0; y < height; y++)
        {
            var src = y * stride;
            var dst = y * (stride + 1);
            raw[dst] = 0; // filter type: none

            for (var x = 0; x < width; x++)
            {
                var s = src + x * 4;
                var d = dst + 1 + x * 4;
                raw[d + 0] = bgra[s + 2]; // R
                raw[d + 1] = bgra[s + 1]; // G
                raw[d + 2] = bgra[s + 0]; // B
                raw[d + 3] = 255;         // A (frame is opaque)
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // color type: RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        WriteChunk(output, "IHDR", header);

        WriteChunk(output, "IDAT", ZlibCompress(raw));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x01);

        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        var adler = Adler32(data);
        compressed.WriteByte((byte)(adler >> 24));
        compressed.WriteByte((byte)(adler >> 16));
        compressed.WriteByte((byte)(adler >> 8));
        compressed.WriteByte((byte)adler);

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        var typeBytes = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;

        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }
}
