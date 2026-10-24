using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Md2Html.Markdown;

/// <summary>
/// HTML5 named character references. CommonMark defers entity syntax to HTML5, so
/// all 2125 names (not just the small HTML4 set) are required. The table is shipped
/// as an embedded resource: name TAB hexadecimal code points.
/// </summary>
internal static class HtmlEntities
{
    private static readonly Dictionary<string, string> Named = LoadNamedEntities();

    /// <summary>
    /// Tries to decode a character reference starting at <paramref name="index"/>.
    /// The returned length includes the leading ampersand and trailing semicolon.
    /// </summary>
    public static bool TryDecodeAt(string text, int index, out string value, out int length)
    {
        value = string.Empty;
        length = 0;
        if (index >= text.Length || text[index] != '&')
        {
            return false;
        }

        int pos = index + 1;
        if (pos < text.Length && text[pos] == '#')
        {
            return TryDecodeNumeric(text, index, ref pos, out value, out length);
        }

        int nameStart = pos;
        while (pos < text.Length && pos - nameStart <= 31 &&
               (char.IsAsciiLetterOrDigit(text[pos])))
        {
            pos++;
        }

        int nameLength = pos - nameStart;
        if (nameLength < 2 || nameLength > 32 || pos >= text.Length || text[pos] != ';')
        {
            return false;
        }

        string name = text[nameStart..pos];
        if (!Named.TryGetValue(name, out value!))
        {
            return false;
        }

        length = pos - index + 1;
        return true;
    }

    private static bool TryDecodeNumeric(
        string text, int ampersand, ref int pos, out string value, out int length)
    {
        // HTML5 numeric character references in the C1 control range are mapped to
        // their Windows-1252 equivalents (&#x80; becomes the euro sign, for example).
        // The reference CommonMark implementation uses the HTML entity decoding rules,
        // so this table is needed for fidelity even though it is rarely seen in prose.
        ReadOnlySpan<int> c1Replacements =
        [
            0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
            0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
            0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
            0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178
        ];
        value = string.Empty;
        length = 0;
        pos++; // '#'

        bool hex = pos < text.Length && (text[pos] == 'x' || text[pos] == 'X');
        if (hex)
        {
            pos++;
        }

        int digitsStart = pos;
        int maxDigits = hex ? 6 : 7;
        while (pos < text.Length && pos - digitsStart < maxDigits)
        {
            char c = text[pos];
            if (hex ? !char.IsAsciiHexDigit(c) : !char.IsAsciiDigit(c))
            {
                break;
            }

            pos++;
        }

        int digitCount = pos - digitsStart;
        if (digitCount == 0 || pos >= text.Length || text[pos] != ';')
        {
            return false;
        }

        // The digit limits are part of the grammar; a longer run is not a
        // character reference at all (the parser must leave it as text).
        if (pos - digitsStart == maxDigits && pos + 1 < text.Length &&
            (hex ? char.IsAsciiHexDigit(text[pos + 1]) : char.IsAsciiDigit(text[pos + 1])))
        {
            return false;
        }

        string digits = text[digitsStart..pos];
        int codePoint = int.Parse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture);

        // HTML5/CommonMark: U+0000 and code points outside Unicode, as well as
        // surrogate halves, are replaced with the replacement character. C1 controls
        // are remapped through the Windows-1252 compatibility table.
        if (codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            codePoint = 0xFFFD;
        }
        else if (codePoint is >= 0x80 and <= 0x9F)
        {
            int replacement = c1Replacements[codePoint - 0x80];
            if (replacement != 0)
            {
                codePoint = replacement;
            }
        }

        value = char.ConvertFromUtf32(codePoint);

        length = pos - ampersand + 1;
        return true;
    }

    private static Dictionary<string, string> LoadNamedEntities()
    {
        Assembly assembly = typeof(HtmlEntities).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("HtmlEntities.txt")
            ?? throw new InvalidOperationException("The embedded HTML entity table is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var result = new Dictionary<string, string>(2200, StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            int tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            string name = line[..tab];
            var characters = new StringBuilder(2);
            foreach (string part in line[(tab + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                characters.Append(char.ConvertFromUtf32(int.Parse(part, NumberStyles.HexNumber)));
            }

            result[name] = characters.ToString();
        }

        return result;
    }
}
