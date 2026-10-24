using System.Net;

namespace WWTools.Calcs.Engine
{
    /// <summary>Pieces every calc report shares: one printable page style, HTML encoding and CSV fields.</summary>
    public static class ReportHtml
    {
        /// <summary>The report stylesheet (without the &lt;style&gt; tags).</summary>
        public const string Styles = @"body{font-family:Segoe UI,Arial,sans-serif;color:#1d2228;margin:24px;font-size:12px}
h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:18px 0 6px;border-bottom:1px solid #ccd;padding-bottom:3px}
.meta{color:#555;margin-bottom:10px}table{border-collapse:collapse;margin:6px 0 12px;width:100%}
th,td{border:1px solid #c9ced6;padding:3px 6px;text-align:left}th{background:#eef1f5}td.n{text-align:right;font-variant-numeric:tabular-nums}
.warn{background:#fff6e0;border:1px solid #e8c56b;padding:6px 10px;margin:8px 0}.note{color:#555}
.zone{page-break-inside:avoid;margin-bottom:18px}.est{color:#a15c00;font-weight:600}
svg text{font-family:Arial,sans-serif}.legend{display:flex;align-items:center;gap:6px;margin:4px 0}
@media print{body{margin:10mm}.zone{page-break-before:auto}}";

        /// <summary>HTML-encodes text; null becomes empty.</summary>
        public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

        private const string PngPrefix = "data:image/png;base64,";

        /// <summary>
        /// Replaces embedded PNG images of at least <paramref name="minBytes"/> with what
        /// <paramref name="transcode"/> makes of them (e.g. a JPEG), when that is at most
        /// <paramref name="maxShare"/> of the PNG's size; null from it keeps the PNG. Returns the
        /// page, and how many images changed and the bytes before and after.
        /// </summary>
        public static (string Html, int Replaced, long BytesBefore, long BytesAfter) ReplaceLargeImages(string html, int minBytes, double maxShare,
            System.Func<byte[], (string Mime, byte[] Data)?> transcode)
        {
            var sb = new System.Text.StringBuilder(html.Length);
            int at = 0, replaced = 0;
            long before = 0, after = 0;
            while (true)
            {
                int start = html.IndexOf(PngPrefix, at, System.StringComparison.Ordinal);
                if (start < 0) break;
                int data = start + PngPrefix.Length;
                int end = html.IndexOf('"', data);
                if (end < 0) break;
                sb.Append(html, at, start - at);
                string base64 = html.Substring(data, end - data);
                byte[]? png = null;
                if (base64.Length / 4 * 3 >= minBytes)
                {
                    try { png = System.Convert.FromBase64String(base64); }
                    catch (System.FormatException) { png = null; }
                }
                (string Mime, byte[] Data)? smaller = null;
                if (png != null)
                {
                    try { smaller = transcode(png); }
                    catch (System.Exception) { smaller = null; }
                }
                if (png != null && smaller.HasValue && smaller.Value.Data.Length <= png.Length * maxShare)
                {
                    sb.Append("data:").Append(smaller.Value.Mime).Append(";base64,").Append(System.Convert.ToBase64String(smaller.Value.Data));
                    replaced++;
                    before += png.Length;
                    after += smaller.Value.Data.Length;
                }
                else
                {
                    sb.Append(html, start, end - start);
                }
                at = end;
            }
            sb.Append(html, at, html.Length - at);
            return (sb.ToString(), replaced, before, after);
        }

        /// <summary>A CSV field, quoted only when it holds a comma, quote or line break.</summary>
        public static string CsvField(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
