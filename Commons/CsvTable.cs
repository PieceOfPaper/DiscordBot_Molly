using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class CsvTable
{
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<CsvRow> Rows { get; }

    private readonly Dictionary<string, int> m_HeaderIndex;

    internal CsvTable(List<string> headers, List<CsvRow> rows)
    {
        Headers = headers;
        Rows = rows;
        m_HeaderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < headers.Count; i++)
        {
            if (!m_HeaderIndex.ContainsKey(headers[i]))
                m_HeaderIndex[headers[i]] = i;
        }
    }

    internal bool TryGetIndex(string header, out int index) => m_HeaderIndex.TryGetValue(header, out index);
}

public sealed class CsvRow
{
    private readonly CsvTable m_Table;
    private readonly string[] m_Fields;

    internal CsvRow(CsvTable table, string[] fields)
    {
        m_Table = table;
        m_Fields = fields;
    }

    public IReadOnlyList<string> Fields => m_Fields;

    public string? this[string header]
    {
        get
        {
            if (m_Table.TryGetIndex(header, out var index) && index < m_Fields.Length)
                return m_Fields[index];
            return null;
        }
    }
}

public static class CsvParser
{
    public static CsvTable Load(string path, char delimiter = ',', bool hasHeader = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("CSV 파일을 찾을 수 없습니다.", path);

        using var reader = new StreamReader(path);
        string? firstLine = reader.ReadLine();
        if (firstLine == null)
            return new CsvTable(new List<string>(), new List<CsvRow>());

        var rows = new List<CsvRow>();
        List<string> headers;
        CsvTable tableForRows;

        if (hasHeader)
        {
            headers = ParseLine(firstLine, delimiter);
            if (headers.Count > 0)
                headers[0] = headers[0].TrimStart('\uFEFF');
            tableForRows = new CsvTable(headers, rows);
        }
        else
        {
            var firstRow = ParseLine(firstLine, delimiter);
            headers = Enumerable.Range(1, firstRow.Count).Select(i => $"col{i}").ToList();
            tableForRows = new CsvTable(headers, rows);
            rows.Add(new CsvRow(tableForRows, firstRow.ToArray()));
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var fields = ParseLine(line, delimiter);
            rows.Add(new CsvRow(tableForRows, fields.ToArray()));
        }

        return tableForRows;
    }

    public static List<string> ParseLine(string line, char delimiter = ',')
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        result.Add(current.ToString());
        return result;
    }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class CsvFieldAttribute : Attribute
{
    public string Name { get; }
    public CsvFieldAttribute(string name) => Name = name;
}

public static class CsvMapper
{
    public static List<T> MapByAttributes<T>(CsvTable table) where T : new()
    {
        var props = typeof(T).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(p => new
            {
                Property = p,
                Attr = (CsvFieldAttribute?)Attribute.GetCustomAttribute(p, typeof(CsvFieldAttribute))
            })
            .Where(x => x.Attr != null && x.Property.CanWrite)
            .ToList();

        var results = new List<T>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var obj = new T();
            foreach (var p in props)
            {
                var raw = row[p.Attr!.Name];
                if (raw == null) continue;

                if (TryConvert(raw, p.Property.PropertyType, out var converted))
                    p.Property.SetValue(obj, converted);
            }
            results.Add(obj);
        }

        return results;
    }

    private static bool TryConvert(string raw, Type targetType, out object? value)
    {
        value = null;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(string))
        {
            value = raw;
            return true;
        }

        var cleaned = raw.Replace(",", "").Trim();
        if (underlying == typeof(int))
        {
            if (int.TryParse(cleaned, out var v)) { value = v; return true; }
            return false;
        }
        if (underlying == typeof(long))
        {
            if (long.TryParse(cleaned, out var v)) { value = v; return true; }
            return false;
        }
        if (underlying == typeof(double))
        {
            if (double.TryParse(cleaned, out var v)) { value = v; return true; }
            return false;
        }
        if (underlying == typeof(decimal))
        {
            if (decimal.TryParse(cleaned, out var v)) { value = v; return true; }
            return false;
        }
        if (underlying == typeof(bool))
        {
            if (bool.TryParse(cleaned, out var v)) { value = v; return true; }
            if (cleaned == "1") { value = true; return true; }
            if (cleaned == "0") { value = false; return true; }
            return false;
        }

        try
        {
            value = Convert.ChangeType(cleaned, underlying);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
