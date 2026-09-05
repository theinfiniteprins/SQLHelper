using System.Text;

namespace SqlHelper.Core.Scripting;

/// <summary>A schema-qualified object name, with quoting handled on the way in and out.</summary>
public sealed record ObjectName(string Schema, string Name)
{
    public const string DefaultSchema = "dbo";

    /// <summary><c>[schema].[name]</c> — safe to drop into T-SQL.</summary>
    public string Bracketed => $"[{Escape(Schema)}].[{Escape(Name)}]";

    /// <summary><c>schema.name</c> — for display and <c>OBJECT_ID()</c> arguments.</summary>
    public string Plain => $"{Schema}.{Name}";

    public override string ToString() => Plain;

    public static ObjectName Parse(string text, string defaultSchema = DefaultSchema)
        => TryParse(text, out ObjectName? name, defaultSchema)
            ? name!
            : throw new FormatException($"'{text}' is not a valid object name.");

    public static bool TryParse(string text, out ObjectName? name, string defaultSchema = DefaultSchema)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        List<string> parts = SplitParts(text.Trim());
        if (parts.Any(string.IsNullOrEmpty) || parts.Count == 0 || parts.Count > 4)
        {
            return false;
        }

        // server.db.schema.name / db.schema.name / schema.name / name — keep the last two.
        string objectName = parts[^1];
        string schema = parts.Count >= 2 ? parts[^2] : defaultSchema;
        if (schema.Length == 0)
        {
            schema = defaultSchema;
        }

        name = new ObjectName(schema, objectName);
        return true;
    }

    private static List<string> SplitParts(string s)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int i = 0;

        while (i < s.Length)
        {
            char c = s[i];
            switch (c)
            {
                case '[':
                    i = ReadDelimited(s, i + 1, ']', current);
                    break;
                case '"':
                    i = ReadDelimited(s, i + 1, '"', current);
                    break;
                case '.':
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    i++;
                    break;
                default:
                    current.Append(c);
                    i++;
                    break;
            }
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }

    private static int ReadDelimited(string s, int start, char closer, StringBuilder into)
    {
        int i = start;
        while (i < s.Length)
        {
            if (s[i] == closer)
            {
                if (i + 1 < s.Length && s[i + 1] == closer)
                {
                    into.Append(closer);
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            into.Append(s[i]);
            i++;
        }

        return i;
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
