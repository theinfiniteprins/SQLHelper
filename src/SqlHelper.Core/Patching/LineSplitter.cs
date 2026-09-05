using System.Text;

namespace SqlHelper.Core.Patching;

/// <summary>One line of text plus the exact line-ending that followed it (empty for the last line, if unterminated).</summary>
internal readonly record struct RawLine(string Text, string Ending)
{
    /// <summary>Content with leading and trailing whitespace removed — used only for comparison, never for output.</summary>
    public string Trimmed => Text.Trim();
}

/// <summary>Splits text into lines without losing the original line-ending bytes, so a patched result can be reassembled exactly.</summary>
internal static class LineSplitter
{
    public static List<RawLine> Split(string text)
    {
        var result = new List<RawLine>();
        var current = new StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                result.Add(new RawLine(current.ToString(), "\r\n"));
                current.Clear();
                i += 2;
            }
            else if (c is '\n' or '\r')
            {
                result.Add(new RawLine(current.ToString(), c.ToString()));
                current.Clear();
                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        if (current.Length > 0 || result.Count == 0)
        {
            result.Add(new RawLine(current.ToString(), string.Empty));
        }

        return result;
    }

    public static string Join(IEnumerable<RawLine> lines)
    {
        var builder = new StringBuilder();
        foreach (RawLine line in lines)
        {
            builder.Append(line.Text).Append(line.Ending);
        }

        return builder.ToString();
    }

    /// <summary>The line ending used by most terminated lines in the text, defaulting to <c>\r\n</c>.</summary>
    public static string DominantEnding(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        return lf > crlf ? "\n" : "\r\n";
    }
}
