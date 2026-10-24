using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MDViewer.Markdown;

/// <summary>
/// Parsing rules for GitHub tables. A table row is a line of cells separated by
/// unescaped pipes. A leading/trailing pipe is optional and not an empty cell.
/// </summary>
internal static class TableParser
{
    private static readonly Regex DelimiterCell = new(@"^:?-+:?$", RegexOptions.Compiled);

    /// <summary>Checks a potential delimiter row such as <c>| :--- | ---: |</c>.</summary>
    public static bool TryParseDelimiterRow(string line, out TableAlignment[] alignments)
    {
        alignments = [];
        List<string> cells = SplitRow(line);
        if (cells.Count == 0)
        {
            return false;
        }

        var parsed = new TableAlignment[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            string cell = cells[i];
            if (!DelimiterCell.IsMatch(cell))
            {
                return false;
            }

            bool left = cell[0] == ':';
            bool right = cell[^1] == ':';
            parsed[i] = left && right ? TableAlignment.Center
                : left ? TableAlignment.Left
                : right ? TableAlignment.Right
                : TableAlignment.None;
        }

        alignments = parsed;
        return true;
    }

    /// <summary>
    /// Returns the header cells (the last line of the open paragraph) and the text
    /// that preceded that line. A table may directly follow ordinary paragraph text;
    /// in that case the earlier text stays a paragraph of its own.
    /// </summary>
    public static bool TrySplitHeader(
        string paragraphContent, out List<string> headerCells, out string precedingText)
    {
        headerCells = [];
        precedingText = string.Empty;

        string content = paragraphContent;
        if (content.EndsWith('\n'))
        {
            content = content[..^1];
        }

        if (content.Length == 0)
        {
            return false;
        }

        int lineStart = content.LastIndexOf('\n') + 1;
        headerCells = SplitRow(content[lineStart..]);
        if (headerCells.Count == 0)
        {
            return false;
        }

        if (lineStart > 0)
        {
            precedingText = content[..lineStart];
        }

        return true;
    }

    /// <summary>Parses one body/header line into trimmed, pipe-unescaped cells.</summary>
    public static List<string> SplitRow(string line)
    {
        int start = 0;
        int end = line.Length;
        while (start < end && line[start] is ' ' or '\t' or '\v' or '\f')
        {
            start++;
        }

        while (end > start && line[end - 1] is ' ' or '\t' or '\v' or '\f')
        {
            end--;
        }

        if (start == end)
        {
            return [];
        }

        bool leadingPipe = line[start] == '|';
        bool trailingPipe = line[end - 1] == '|' && HasEvenPrecedingBackslashes(line, end - 1);

        var rawCells = new List<string>();
        var current = new StringBuilder();
        int i = start;
        while (i < end)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < end)
            {
                char next = line[i + 1];
                if (next == '|')
                {
                    // The table layer removes the escape; the inline parser must
                    // never see it, otherwise a pipe inside a code span would stay
                    // escaped (GFM explicitly allows \| inside code spans).
                    current.Append('|');
                    i += 2;
                    continue;
                }

                if (StringUtil.IsAsciiPunctuation(next))
                {
                    // Other escapes stay intact for the inline parser.
                    current.Append(c).Append(next);
                    i += 2;
                    continue;
                }
            }

            if (c == '|')
            {
                rawCells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }

            i++;
        }

        rawCells.Add(current.ToString());

        // A leading pipe produces an empty piece before the first cell and a
        // trailing pipe produces one after the last cell. Neither is a cell.
        if (leadingPipe && rawCells.Count > 0)
        {
            rawCells.RemoveAt(0);
        }

        if (trailingPipe && rawCells.Count > 0)
        {
            rawCells.RemoveAt(rawCells.Count - 1);
        }

        for (int cell = 0; cell < rawCells.Count; cell++)
        {
            rawCells[cell] = rawCells[cell].Trim(' ', '\t', '\v', '\f', '\r', '\n');
        }

        return rawCells;
    }

    private static bool HasEvenPrecedingBackslashes(string line, int pipeIndex)
    {
        int count = 0;
        for (int i = pipeIndex - 1; i >= 0 && line[i] == '\\'; i--)
        {
            count++;
        }

        return count % 2 == 0;
    }
}
