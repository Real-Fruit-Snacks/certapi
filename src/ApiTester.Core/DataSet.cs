using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Loads a data-driven test dataset — a CSV (header row + rows) or a JSON array of objects —
/// into a list of string rows whose columns are available as <c>{{variables}}</c> per iteration.</summary>
public static class DataSet
{
    /// <summary>Load a dataset by file extension (.json → JSON array, otherwise CSV).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Data file not found: {path}");
        // UTF-8 (with or without a byte-order mark) or BOM-marked UTF-16, as PowerShell writes them;
        // anything else is refused and said so: Excel's "CSV (Comma delimited)" is Windows-1252, and
        // reading it as UTF-8 turned Müller into M\uFFFDller on the wire with nothing to show for it.
        string text;
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"{path} is not UTF-8 text. Save it as UTF-8 (in Excel: \"CSV UTF-8 (Comma delimited)\").");
        }
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(text) : ParseCsv(text);
    }

    /// <summary>Parse CSV with a header row; quoted fields may contain commas, line breaks and ""
    /// escapes (RFC 4180). Blank lines are skipped; a leading byte-order mark is not a header.</summary>
    public static List<IReadOnlyDictionary<string, string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var records = Records(text).Where(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
        if (records.Count < 2) return rows;

        var headers = records[0].Select(h => h.Trim()).ToList();
        for (int i = 1; i < records.Count; i++)
        {
            var fields = records[i];
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < headers.Count; c++)
                if (headers[c].Length > 0)
                    row[headers[c]] = c < fields.Count ? fields[c] : "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The records of a CSV text, read quote-aware over the whole text so a quoted field
    /// may span lines.</summary>
    private static IEnumerable<List<string>> Records(string text)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, fieldWasQuoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            // A quote opens a quoted field only at the start of one, after any spaces (a
            // hand-written row like  1, "x,y"); elsewhere (5" screen) it is just a character,
            // as in RFC 4180 and every spreadsheet.
            else if (ch == '"' && !fieldWasQuoted && IsBlank(sb)) { sb.Clear(); inQuotes = fieldWasQuoted = true; }
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); fieldWasQuoted = false; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear(); fieldWasQuoted = false;
                yield return fields;
                fields = new List<string>();
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            yield return fields;
        }
    }

    /// <summary>Parse a JSON array of objects; each value is stringified (a string as-is, else its JSON).</summary>
    public static List<IReadOnlyDictionary<string, string>> ParseJson(string text)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
                row[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => prop.Value.GetRawText()
                };
            rows.Add(row);
        }
        return rows;
    }

    private static bool IsBlank(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] is not (' ' or '\t')) return false;
        return true;
    }
}
