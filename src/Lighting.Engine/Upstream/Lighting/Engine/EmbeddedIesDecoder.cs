using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace WWTools.Lighting.Engine
{
    /// <summary>
    /// Turns the text Revit keeps in a light family's Photometric Web File cache into IES text.
    /// The cache is not always the plain file: this tries plain text, Base64 (plain, GZip, zlib
    /// or Deflate inside), hex, and an IES body whose header lines were dropped.
    /// </summary>
    public static class EmbeddedIesDecoder
    {
        /// <summary>Returns the IES text, or null; <paramref name="how"/> says which encoding worked.</summary>
        public static string? Decode(string? cache, out string how)
        {
            how = string.Empty;
            if (string.IsNullOrWhiteSpace(cache)) return null;
            string text = cache!;

            if (LooksLikeIes(text))
            {
                how = "plain text";
                return text;
            }

            // The file's bytes read as UTF-16, so each char holds two ASCII bytes ("IE" is U+4549).
            if (text.Any(c => c > 0xFF))
            {
                string unpacked = Encoding.GetEncoding(28591).GetString(Encoding.Unicode.GetBytes(text)).TrimEnd('\0');
                if (LooksLikeIes(unpacked))
                {
                    how = "bytes packed as UTF-16";
                    return unpacked;
                }
            }

            string compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            byte[]? bytes = TryBase64(compact);
            if (bytes != null)
            {
                string? fromBytes = FromBytes(bytes, out string inner);
                if (fromBytes != null)
                {
                    how = "Base64" + (inner.Length > 0 ? " + " + inner : string.Empty);
                    return fromBytes;
                }
            }

            bytes = TryHex(compact);
            if (bytes != null)
            {
                string? fromBytes = FromBytes(bytes, out string inner);
                if (fromBytes != null)
                {
                    how = "hex" + (inner.Length > 0 ? " + " + inner : string.Empty);
                    return fromBytes;
                }
            }

            // Just the numbers (the keyword and TILT lines left out).
            string withTilt = "TILT=NONE\n" + text;
            try
            {
                IesPhotometry p = IesPhotometry.Parse(withTilt);
                if (p.ComputedLumens > 0)
                {
                    how = "numeric body without a TILT line";
                    return withTilt;
                }
            }
            catch (FormatException)
            {
            }
            return null;
        }

        /// <summary>The first characters, with line breaks shown, for the log.</summary>
        public static string Preview(string? text, int length = 160)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            string head = text!.Length > length ? text.Substring(0, length) + "…" : text;
            return head.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static bool LooksLikeIes(string text) => text.IndexOf("TILT", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string? FromBytes(byte[] bytes, out string how)
        {
            how = string.Empty;
            string plain = Latin1(bytes);
            if (LooksLikeIes(plain)) return plain;

            foreach ((string name, Func<Stream, Stream> open) in new (string, Func<Stream, Stream>)[]
            {
                ("GZip", s => new GZipStream(s, CompressionMode.Decompress)),
                ("Deflate", s => new DeflateStream(s, CompressionMode.Decompress)),
                ("zlib", s => { s.ReadByte(); s.ReadByte(); return new DeflateStream(s, CompressionMode.Decompress); }),
            })
            {
                try
                {
                    using (var input = new MemoryStream(bytes))
                    using (Stream decompressed = open(input))
                    using (var output = new MemoryStream())
                    {
                        decompressed.CopyTo(output);
                        string text = Latin1(output.ToArray());
                        if (LooksLikeIes(text))
                        {
                            how = name;
                            return text;
                        }
                    }
                }
                catch
                {
                    // not this encoding
                }
            }
            return null;
        }

        private static string Latin1(byte[] bytes)
        {
            // UTF-16 text shows up as every other byte zero.
            if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0) return Encoding.Unicode.GetString(bytes);
            return Encoding.GetEncoding(28591).GetString(bytes);
        }

        private static byte[]? TryBase64(string compact)
        {
            if (compact.Length < 8 || compact.Length % 4 != 0) return null;
            foreach (char c in compact)
            {
                if (!(char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=')) return null;
            }
            try { return Convert.FromBase64String(compact); }
            catch (FormatException) { return null; }
        }

        private static byte[]? TryHex(string compact)
        {
            if (compact.Length < 8 || compact.Length % 2 != 0) return null;
            if (!compact.All(Uri.IsHexDigit)) return null;
            var bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
