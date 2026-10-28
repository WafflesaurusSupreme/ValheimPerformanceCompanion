using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace Companion.Core;

public static class Csv
{
    public static IEnumerable<Dictionary<string, string>> Read(string path, int maximumRows = 2_000_000)
    {
        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("CSV exceeds the 512 MB import limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var parser = new TextFieldParser(stream) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        string[]? header = parser.ReadFields();
        if (header is null || header.Length > 256 || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            throw new InvalidDataException("Missing, duplicate or excessive CSV columns.");
        int row = 0;
        while (!parser.EndOfData)
        {
            if (++row > maximumRows) throw new InvalidDataException("CSV exceeds the row limit.");
            string[]? fields;
            try { fields = parser.ReadFields(); }
            catch (MalformedLineException ex) { throw new InvalidDataException($"Malformed CSV near row {row + 1}.", ex); }
            if (fields is null) break;
            if (fields.Length != header.Length) throw new InvalidDataException($"Incomplete CSV row {row + 1}; expected {header.Length} columns.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < header.Length; i++) result.Add(header[i], fields[i]);
            yield return result;
        }
    }
    public static double? Number(this Dictionary<string, string> row, string column) =>
        row.TryGetValue(column, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && double.IsFinite(value) ? value : null;
    public static string Text(this Dictionary<string, string> row, string column) => row.GetValueOrDefault(column, "");
    public static string Escape(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
