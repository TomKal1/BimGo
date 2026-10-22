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

        /// <summary>A CSV field, quoted only when it holds a comma, quote or line break.</summary>
        public static string CsvField(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
