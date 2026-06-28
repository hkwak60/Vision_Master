using System.Text;

namespace KickoutMonitor.Infrastructure;

public static class CsvSupport
{
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }
        values.Add(value.ToString());
        return values;
    }

    public static IReadOnlyList<string> UniqueHeaders(IReadOnlyList<string> headers)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return headers.Select((header, index) =>
        {
            var name = header.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(name)) name = $"COLUMN_{index + 1}";
            counts.TryGetValue(name, out var count);
            counts[name] = ++count;
            return count == 1 ? name : $"{name}#{count}";
        }).ToArray();
    }
}
