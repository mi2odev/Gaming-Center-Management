using System.Globalization;
using System.Text;

namespace GamingCenter.Application.Common;

/// <summary>
/// Minimal RFC 4180 CSV writer. Files are UTF-8 with BOM so Excel opens accents and "DA" correctly.
/// </summary>
public static class CsvWriter
{
    public sealed record Column<T>(string Header, Func<T, object?> Value);

    public static string Build<T>(IEnumerable<T> rows, IReadOnlyList<Column<T>> columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => Escape(c.Header))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", columns.Select(c => Escape(Format(c.Value(row))))));
        return sb.ToString();
    }

    public static async Task WriteAsync<T>(string path, IEnumerable<T> rows, IReadOnlyList<Column<T>> columns, CancellationToken ct = default)
    {
        var text = Build(rows, columns);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Escape(string s)
    {
        // Neutralise spreadsheet formulas injected through names/notes.
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
