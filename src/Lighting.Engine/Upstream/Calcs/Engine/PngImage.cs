using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace WWTools.Calcs.Engine
{
    /// <summary>An RGB picture that can save itself as a PNG (no imaging library needed), for reports.</summary>
    public sealed class PngImage
    {
        public PngImage(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 3];
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Row-major RGB, top row first.</summary>
        public byte[] Pixels { get; }

        public void Fill(byte r, byte g, byte b)
        {
            for (int i = 0; i < Pixels.Length; i += 3)
            {
                Pixels[i] = r;
                Pixels[i + 1] = g;
                Pixels[i + 2] = b;
            }
        }

        public void Set(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int i = (y * Width + x) * 3;
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
        }

        /// <summary>The picture as PNG bytes (8-bit RGB, filtered per row, zlib-deflated).</summary>
        public byte[] ToPng() => ToPng(null);

        /// <summary>PNG bytes with optional UTF-8 iTXt metadata, preserving the original parameterless API.</summary>
        public byte[] ToPng(IEnumerable<KeyValuePair<string, string>>? metadata)
        {
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var header = new byte[13];
                WriteBigEndian(header, 0, (uint)Width);
                WriteBigEndian(header, 4, (uint)Height);
                header[8] = 8;  // bit depth
                header[9] = 2;  // colour type: RGB
                Chunk(png, "IHDR", header);

                if (metadata != null) foreach (var entry in metadata)
                {
                    if (string.IsNullOrEmpty(entry.Key) || entry.Key.Length > 79 || entry.Key.IndexOf('\0') >= 0 || entry.Key.Any(c => c < 32 || c > 126))
                        throw new ArgumentException("PNG metadata keywords must contain 1–79 printable ASCII characters.");
                    byte[] key = Encoding.ASCII.GetBytes(entry.Key), value = Encoding.UTF8.GetBytes(entry.Value ?? string.Empty);
                    var text = new byte[key.Length + 5 + value.Length];
                    Buffer.BlockCopy(key, 0, text, 0, key.Length);
                    // Keyword terminator, compression flag/method, empty language and translated keyword.
                    Buffer.BlockCopy(value, 0, text, key.Length + 5, value.Length);
                    Chunk(png, "iTXt", text);
                }

                Chunk(png, "IDAT", Zlib(Filtered()));
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        /// <summary>A data: URI for an &lt;img&gt; tag.</summary>
        public string ToDataUri() => "data:image/png;base64," + Convert.ToBase64String(ToPng());

        /// <summary>
        /// The rows with a PNG filter each: whichever of None, Sub, Up, Average and Paeth gives the
        /// smallest sum of absolute differences (the usual heuristic; smooth shading shrinks a lot).
        /// </summary>
        private byte[] Filtered()
        {
            int stride = Width * 3;
            var raw = new byte[(stride + 1) * Height];
            var candidate = new byte[stride];
            var best = new byte[stride];
            var zero = new byte[stride];
            for (int y = 0; y < Height; y++)
            {
                int row = y * stride;
                int above = y > 0 ? row - stride : -1;
                long bestScore = long.MaxValue;
                byte bestType = 0;
                for (byte type = 0; type <= 4; type++)
                {
                    long score = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int x = Pixels[row + i];
                        int a = i >= 3 ? Pixels[row + i - 3] : 0;
                        int b = above >= 0 ? Pixels[above + i] : 0;
                        int c = i >= 3 && above >= 0 ? Pixels[above + i - 3] : 0;
                        int predicted = type == 0 ? 0 : type == 1 ? a : type == 2 ? b : type == 3 ? (a + b) / 2 : Paeth(a, b, c);
                        byte value = (byte)(x - predicted);
                        candidate[i] = value;
                        score += value < 128 ? value : 256 - value;
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestType = type;
                        Buffer.BlockCopy(candidate, 0, best, 0, stride);
                    }
                }
                raw[y * (stride + 1)] = bestType;
                Buffer.BlockCopy(best, 0, raw, y * (stride + 1) + 1, stride);
            }
            return raw;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static byte[] Zlib(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78); // deflate, 32K window
                output.WriteByte(0x9C); // default compression; header checksum
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(data, 0, data.Length);
                var adler = new byte[4];
                WriteBigEndian(adler, 0, Adler32(data));
                output.Write(adler, 0, 4);
                return output.ToArray();
            }
        }

        private static void Chunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            stream.Write(length, 0, 4);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
            crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc);
            stream.Write(crcBytes, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        internal static uint Crc32(byte[] data, uint crc)
        {
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        internal static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
