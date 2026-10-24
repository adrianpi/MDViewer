using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Md2Html.Markdown;

/// <summary>Small parsing helpers shared by the block, inline, and HTML layers.</summary>
internal static class StringUtil
{
    private const string EscapablePunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    // mdurl-compatible set: characters that may stay unescaped in a URI.
    // It is encodeURIComponent's unreserved set plus URI syntax characters.
    private const string UriSafe =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
        "-_.!~*'()" + ";,/?:@&=+$#";

    private static readonly Regex WhitespaceRun = new(@"[ \t\r\n]+", RegexOptions.Compiled);

    public static bool IsAsciiPunctuation(char c) =>
        c < 128 && EscapablePunctuation.IndexOf(c) >= 0;

    public static bool IsBlank(string s)
    {
        foreach (char c in s)
        {
            if (c is not (' ' or '\t' or '\f' or '\v'))
            {
                return false;
            }
        }

        return true;
    }

    public static string EscapeHtml(string value)
    {
        if (value.IndexOfAny(['&', '<', '>', '"']) < 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 16);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': result.Append("&amp;"); break;
                case '<': result.Append("&lt;"); break;
                case '>': result.Append("&gt;"); break;
                case '"': result.Append("&quot;"); break;
                default: result.Append(c); break;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Replaces backslash escapes and entity references with their literal values.
    /// Used for link destinations, titles, and fenced code info strings.
    /// </summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0 && s.IndexOf('&') < 0)
        {
            return s;
        }

        var result = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && IsAsciiPunctuation(s[i + 1]))
            {
                result.Append(s[++i]);
            }
            else if (c == '&' && HtmlEntities.TryDecodeAt(s, i, out string decoded, out int length))
            {
                result.Append(decoded);
                i += length - 1;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Replaces entity references only (no backslash escapes). Autolinks use this
    /// because a backslash inside a URL is a literal character there.
    /// </summary>
    public static string DecodeEntities(string s)
    {
        if (s.IndexOf('&') < 0)
        {
            return s;
        }

        var result = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '&' && HtmlEntities.TryDecodeAt(s, i, out string decoded, out int length))
            {
                result.Append(decoded);
                i += length - 1;
            }
            else
            {
                result.Append(s[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Reference labels are case-insensitive and internal whitespace is collapsed.
    /// Upper-casing after lower-casing gives a locale-independent case fold.
    /// </summary>
    public static string NormalizeReference(string label)
    {
        string inner = label;
        if (inner.Length >= 2 && inner[0] == '[' && inner[^1] == ']')
        {
            inner = inner[1..^1];
        }

        inner = WhitespaceRun.Replace(inner.Trim(), " ");

        // Unicode full case folding is not the same as .NET's invariant upper-case:
        // for example ẞ folds to SS, while ToUpperInvariant keeps it as one letter.
        return FoldCase(inner);
    }

    private static string FoldCase(string value)
    {
        string lowered = value.ToLowerInvariant();
        var result = new StringBuilder(lowered.Length);

        foreach (char c in lowered)
        {
            switch (c)
            {
                case 'ß': result.Append("SS"); break;
                case 'ﬀ': result.Append("FF"); break;
                case 'ﬁ': result.Append("FI"); break;
                case 'ﬂ': result.Append("FL"); break;
                case 'ﬃ': result.Append("FFI"); break;
                case 'ﬄ': result.Append("FFL"); break;
                case 'ﬅ':
                case 'ﬆ': result.Append("ST"); break;
                case 'ŉ': result.Append("\u02BCN"); break;

                // The dotless i intentionally has no ASCII upper-case mapping in
                // the reference algorithm's case fold.
                case '\u0131': result.Append('\u0131'); break;
                default: result.Append(char.ToUpperInvariant(c)); break;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Percent-encodes a URL the way the CommonMark reference implementation does.
    /// Existing percent escapes are preserved; spaces become %20; non-ASCII text is
    /// encoded as UTF-8. Without this, browsers would silently normalise some links
    /// differently from the reference renderer.
    /// </summary>
    public static string NormalizeUri(string uri)
    {
        var result = new StringBuilder(uri.Length + 16);
        Span<byte> bytes = stackalloc byte[4];
        int i = 0;
        while (i < uri.Length)
        {
            char c = uri[i];
            if (c == '%' && i + 2 < uri.Length && IsHex(uri[i + 1]) && IsHex(uri[i + 2]))
            {
                result.Append(c);
                result.Append(uri[i + 1]);
                result.Append(uri[i + 2]);
                i += 3;
                continue;
            }

            if (c < 128)
            {
                if (UriSafe.IndexOf(c) >= 0)
                {
                    result.Append(c);
                }
                else
                {
                    AppendPercentEncodedByte(result, (byte)c);
                }

                i++;
                continue;
            }

            int codePoint = char.ConvertToUtf32(uri, i);
            int charCount = char.IsSurrogatePair(uri, i) ? 2 : 1;
            int byteCount = Encoding.UTF8.GetBytes(uri.AsSpan(i, charCount), bytes);
            for (int b = 0; b < byteCount; b++)
            {
                AppendPercentEncodedByte(result, bytes[b]);
            }

            i += charCount;
        }

        return result.ToString();
    }

    public static string FromCodePoint(int codePoint) => char.ConvertFromUtf32(codePoint);

    public static bool IsUnicodeWhitespace(int codePoint)
    {
        if (codePoint is '\t' or '\n' or '\f' or '\r' or ' ')
        {
            return true;
        }

        if (codePoint < 0 || codePoint > 0x10FFFF)
        {
            return false;
        }

        return Rune.GetUnicodeCategory(new Rune(codePoint)) == UnicodeCategory.SpaceSeparator;
    }

    public static bool IsUnicodePunctuation(int codePoint)
    {
        if (codePoint is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126)
        {
            return true;
        }

        if (codePoint < 0 || codePoint > 0x10FFFF)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(new Rune(codePoint));
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
    }

    public static string TrimAscii(string value)
    {
        int start = 0;
        while (start < value.Length && IsAsciiLineWhitespace(value[start]))
        {
            start++;
        }

        int end = value.Length - 1;
        while (end >= start && IsAsciiLineWhitespace(value[end]))
        {
            end--;
        }

        return value[start..(end + 1)];
    }

    public static bool IsAsciiLineWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static void AppendPercentEncodedByte(StringBuilder target, byte value)
    {
        const string hex = "0123456789ABCDEF";
        target.Append('%');
        target.Append(hex[value >> 4]);
        target.Append(hex[value & 0xF]);
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
