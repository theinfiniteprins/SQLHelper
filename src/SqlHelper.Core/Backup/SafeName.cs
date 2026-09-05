using System.Text.RegularExpressions;

namespace SqlHelper.Core.Backup;

/// <summary>Turns arbitrary text into a safe file/folder name component.</summary>
public static partial class SafeName
{
    [GeneratedRegex(@"-{2,}")]
    private static partial Regex DashRun();

    public static string ForPath(string text, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unnamed";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = new List<char>(text.Length);

        foreach (char c in text.Trim())
        {
            if (c is ' ' or '\t' or '/' or '\\')
            {
                chars.Add('-');
            }
            else if (Array.IndexOf(invalid, c) < 0)
            {
                chars.Add(c);
            }
        }

        string result = DashRun().Replace(new string(chars.ToArray()), "-").Trim('-', '.', ' ');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].Trim('-', '.');
        }

        return result.Length == 0 ? "unnamed" : result;
    }

    /// <summary>A sortable timestamp folder prefix, e.g. <c>2026-09-04_14-30-05</c>.</summary>
    public static string TimestampFolder(DateTimeOffset now) => now.ToString("yyyy-MM-dd_HH-mm-ss");
}
