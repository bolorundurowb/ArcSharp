using System.Text;

namespace ArcSharp.Frontend;

// The legacy `weak` field modifier is not C#. Rewrite it to the marker
// attribute before Roslyn parses the file, so the two samples that still use
// it keep compiling. Strings, characters, and comments are left alone.
static class LegacyWeak
{
    public const string Attribute = "[global::ArcSharp.Runtime.Weak] ";

    public static string Rewrite(string source)
    {
        var sb = new StringBuilder(source.Length);
        var previous = "";
        var modifierStart = 0;
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                if (end < 0)
                {
                    end = source.Length;
                }

                sb.Append(source, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                sb.Append(source, i, end - i);
                i = end;
                continue;
            }

            if (c == '"')
            {
                i = CopyQuoted(source, sb, i, '"');
                previous = "";
                continue;
            }

            if (c == '\'')
            {
                i = CopyQuoted(source, sb, i, '\'');
                previous = "";
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                var word = source.Substring(start, i - start);
                if (previous is "" or "{" or "}" or ";")
                {
                    modifierStart = sb.Length;
                }

                // Attributes precede modifiers, so `public weak T f` becomes
                // `[Weak] public T f` rather than `public [Weak] T f`.
                if (word == "weak" && IsModifierContext(previous))
                {
                    sb.Insert(modifierStart, Attribute);
                }
                else
                {
                    sb.Append(word);
                }

                previous = word;
                continue;
            }

            sb.Append(c);
            previous = c.ToString();
            i++;
        }

        return sb.ToString();
    }

    private static bool IsModifierContext(string previous) =>
        previous is "" or "{" or "}" or ";" or "public" or "private" or "protected"
            or "internal" or "static" or "readonly" or "volatile" or "new";

    private static int CopyQuoted(string source, StringBuilder sb, int i, char quote)
    {
        sb.Append(source[i]);
        i++;
        while (i < source.Length)
        {
            var c = source[i];
            sb.Append(c);
            i++;
            if (c == '\\' && i < source.Length)
            {
                sb.Append(source[i]);
                i++;
                continue;
            }

            if (c == quote)
            {
                break;
            }
        }

        return i;
    }
}
