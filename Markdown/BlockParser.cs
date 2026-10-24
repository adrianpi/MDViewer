using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Md2Html.Markdown;

/// <summary>
/// Phase 1 of CommonMark parsing: build the block structure line by line.
/// The algorithm follows the strategy in the specification appendix (and the
/// commonmark.js reference implementation): maintain a stack of open blocks,
/// match their continuation markers, then try to open new blocks.
/// </summary>
internal sealed class BlockParser
{
    private const int CodeIndent = 4;

    private static readonly Regex HtmlBlockOpen1 = new(
        @"^<(?:script|pre|textarea|style)(?:\s|>|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlBlockOpen6 = new(
        @"^</?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|" +
        @"dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|" +
        @"h[123456]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|nav|noframes|" +
        @"ol|optgroup|option|p|param|section|search|summary|table|tbody|td|tfoot|th|thead|title|" +
        @"tr|track|ul)(?:\s|/?>|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlBlockOpen7 = new(
        "^(?:" + HtmlTag.OpenTag + "|" + HtmlTag.CloseTag + @")\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlBlockClose1 = new(
        @"</(?:script|pre|textarea|style)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlBlockClose2 = new(@"-->", RegexOptions.Compiled);
    private static readonly Regex HtmlBlockClose3 = new(@"\?>", RegexOptions.Compiled);
    private static readonly Regex HtmlBlockClose4 = new(@">", RegexOptions.Compiled);
    private static readonly Regex HtmlBlockClose5 = new(@"\]\]>", RegexOptions.Compiled);

    private static readonly Regex ThematicBreak = new(
        @"^(?:\*[ \t]*){3,}$|^(?:_[ \t]*){3,}$|^(?:-[ \t]*){3,}$", RegexOptions.Compiled);
    private static readonly Regex MaybeSpecial = new(
        @"^[#`~*+_=<>0-9|:\-]", RegexOptions.Compiled);
    private static readonly Regex NonSpace = new(@"[^ \t\f\v\r\n]", RegexOptions.Compiled);
    private static readonly Regex BulletListMarker = new(@"^[*+\-]", RegexOptions.Compiled);
    private static readonly Regex OrderedListMarker = new(@"^(\d{1,9})([.)])", RegexOptions.Compiled);
    private static readonly Regex AtxHeadingMarker = new(@"^#{1,6}(?:[ \t]+|$)", RegexOptions.Compiled);
    private static readonly Regex CodeFence = new(@"^`{3,}(?!.*`)|^~{3,}", RegexOptions.Compiled);
    private static readonly Regex ClosingCodeFence = new(@"^(?:`{3,}|~{3,})(?=[ \t]*$)", RegexOptions.Compiled);
    private static readonly Regex SetextHeadingLine = new(@"^(?:=+|-+)[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex LineEnding = new(@"\r\n|\n|\r", RegexOptions.Compiled);
    private static readonly Regex ClosingHashSequence = new(@"[ \t]+#+[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex OnlyHashSequence = new(@"^[ \t]*#+[ \t]*$", RegexOptions.Compiled);

    private readonly MarkdownOptions _options;
    private readonly InlineParser _inlineParser;
    private readonly Dictionary<string, LinkReference> _references = new(StringComparer.Ordinal);

    private MarkdownNode _document = null!;
    private MarkdownNode? _tip;
    private MarkdownNode? _oldTip;
    private MarkdownNode? _lastMatchedContainer;

    private string _currentLine = string.Empty;
    private int _lineNumber;
    private int _offset;
    private int _column;
    private int _nextNonspace;
    private int _nextNonspaceColumn;
    private int _indent;
    private int _lastLineLength;
    private bool _indented;
    private bool _blank;
    private bool _partiallyConsumedTab;
    private bool _allClosed;

    public BlockParser(MarkdownOptions options)
    {
        _options = options;
        _inlineParser = new InlineParser(options);
    }

    public MarkdownNode Parse(string input)
    {
        _document = new MarkdownNode(NodeType.Document)
        {
            SourceStartLine = 1,
            SourceEndLine = 1,
            IsOpen = true
        };
        _tip = _document;
        _oldTip = _document;
        _lastMatchedContainer = _document;
        _lineNumber = 0;
        _lastLineLength = 0;
        _offset = 0;
        _column = 0;
        _allClosed = true;
        _references.Clear();

        // CommonMark treats CRLF, LF, and CR as line endings. A NUL byte has no
        // meaning in a document, so replacing it with U+FFFD prevents it from
        // travelling into the generated HTML or file names.
        string normalized = input.Replace('\0', '\uFFFD');
        string[] lines = LineEnding.Split(normalized);

        int count = lines.Length;
        if (normalized.EndsWith('\n'))
        {
            // The trailing newline produces a final empty element that is not a
            // document line (otherwise every file would end with a blank line).
            count--;
        }

        for (int i = 0; i < count; i++)
        {
            IncorporateLine(lines[i]);
        }

        while (_tip is not null)
        {
            Finalize(_tip, count);
        }

        _inlineParser.References = _references;
        if (_options.TaskLists)
        {
            TaskListProcessor.Transform(_document);
        }

        ProcessInlines(_document);
        if (_options.AutolinkLiterals)
        {
            AutolinkLiteralProcessor.Process(_document);
        }

        return _document;
    }

    // ------------------------------------------------------------------
    // Line processing
    // ------------------------------------------------------------------

    private void IncorporateLine(string line)
    {
        MarkdownNode container = _document;
        _oldTip = _tip;
        _offset = 0;
        _column = 0;
        _blank = false;
        _partiallyConsumedTab = false;
        _lineNumber++;
        _currentLine = line;

        // Match the continuation markers of all open blocks, outermost first.
        bool allMatched = true;
        while (container.LastChild is { IsOpen: true } lastChild)
        {
            container = lastChild;
            FindNextNonspace();

            switch (Continue(container))
            {
                case 0:
                    break;
                case 1:
                    allMatched = false;
                    break;
                case 2:
                    // A closing code fence consumed the line completely.
                    return;
                default:
                    throw new InvalidOperationException("Block continuation returned an invalid value.");
            }

            if (!allMatched)
            {
                container = container.Parent!;
                break;
            }
        }

        _allClosed = ReferenceEquals(container, _oldTip);
        _lastMatchedContainer = container;

        bool matchedLeaf = container.Type != NodeType.Paragraph && AcceptsLines(container.Type);

        // Try to open new blocks as long as the last matched block can contain them.
        while (!matchedLeaf)
        {
            FindNextNonspace();

            // Fast path: most text lines cannot start a new block. Tables are
            // exempt because a plain text line is a valid table data row.
            if (!_indented && container.Type != NodeType.Table &&
                !MaybeSpecial.IsMatch(line[_nextNonspace..]))
            {
                AdvanceNextNonspace();
                break;
            }

            int result = 0;
            result = TryStartBlockQuote(container);
            if (result == 0) result = TryStartAtxHeading(container);
            if (result == 0) result = TryStartFencedCode(container);
            if (result == 0) result = TryStartHtmlBlock(container);
            if (result == 0) result = TryStartSetextHeading(container);
            if (result == 0) result = TryStartThematicBreak(container);
            if (result == 0) result = TryStartListItem(container);
            if (result == 0) result = TryStartIndentedCode(container);

            // GitHub extensions are attempted after all standard block starts,
            // matching cmark-gfm's extension dispatch order.
            if (result == 0) result = TryStartTableHeader(container);
            if (result == 0) result = TryStartTableRow(container);

            if (result == 0)
            {
                // Nothing matched: the remainder belongs to the current container.
                AdvanceNextNonspace();
                break;
            }

            container = _tip!;
            if (result == 2)
            {
                matchedLeaf = true;
            }
        }

        if (!_allClosed && !_blank && _tip!.Type == NodeType.Paragraph)
        {
            // Lazy paragraph continuation: a block quote marker or list indentation
            // may be omitted on a paragraph continuation line.
            AddLine();
        }
        else
        {
            CloseUnmatchedBlocks();

            if (AcceptsLines(container.Type))
            {
                AddLine();

                // HTML blocks of types 1-5 also have an explicit end marker.
                if (container.Type == NodeType.HtmlBlock &&
                    container.HtmlBlockType is >= 1 and <= 5 &&
                    HtmlBlockEnds(container.HtmlBlockType, _currentLine[_offset..]))
                {
                    _lastLineLength = line.Length;
                    Finalize(container, _lineNumber);
                }
            }
            else if (_offset < line.Length && !_blank)
            {
                container = AddChild(NodeType.Paragraph, _offset);
                AdvanceNextNonspace();
                AddLine();
            }
        }

        _lastLineLength = line.Length;
    }

    private void AddLine()
    {
        if (_partiallyConsumedTab)
        {
            _offset += 1; // skip the tab itself
            int spacesToTabStop = 4 - (_column % 4);
            _tip!.StringContent += new string(' ', spacesToTabStop);
        }

        _tip!.StringContent += _currentLine[_offset..];
        _tip.StringContent += "\n";
    }

    private MarkdownNode AddChild(NodeType type, int offset)
    {
        while (!CanContain(_tip!.Type, type))
        {
            Finalize(_tip, _lineNumber - 1);
        }

        var node = new MarkdownNode(type)
        {
            SourceStartLine = _lineNumber,
            SourceEndLine = _lineNumber,
            IsOpen = true
        };
        _tip.AppendChild(node);
        _tip = node;
        return node;
    }

    private void CloseUnmatchedBlocks()
    {
        if (_allClosed)
        {
            return;
        }

        while (!ReferenceEquals(_oldTip, _lastMatchedContainer))
        {
            MarkdownNode parent = _oldTip!.Parent!;
            Finalize(_oldTip, _lineNumber - 1);
            _oldTip = parent;
        }

        _allClosed = true;
    }

    private void Finalize(MarkdownNode block, int lineNumber)
    {
        MarkdownNode? parent = block.Parent;
        block.IsOpen = false;
        block.SourceEndLine = lineNumber;

        switch (block.Type)
        {
            case NodeType.Document:
                RemoveLinkReferenceDefinitions(block);
                break;

            case NodeType.List:
                FinalizeList(block);
                break;

            case NodeType.Item:
                if (block.LastChild is not null)
                {
                    block.SourceEndLine = block.LastChild.SourceEndLine;
                }
                else
                {
                    block.SourceEndLine = block.SourceStartLine;
                }

                break;

            case NodeType.CodeBlock:
                FinalizeCodeBlock(block);
                break;

            case NodeType.HtmlBlock:
                block.Literal = block.StringContent.TrimEnd('\n');
                block.StringContent = string.Empty;
                break;
        }

        _tip = parent;
    }

    private void FinalizeList(MarkdownNode block)
    {
        // A list is loose if any item is followed by a blank line, or if any
        // item's direct children are separated by a blank line.
        foreach (MarkdownNode item in block.Children)
        {
            if (item.Next is not null && EndsWithBlankLine(item))
            {
                block.ListData!.IsTight = false;
                break;
            }

            foreach (MarkdownNode subItem in item.Children)
            {
                if (subItem.Next is not null && EndsWithBlankLine(subItem))
                {
                    block.ListData!.IsTight = false;
                    break;
                }
            }

            if (!block.ListData!.IsTight)
            {
                break;
            }
        }

        if (block.LastChild is not null)
        {
            block.SourceEndLine = block.LastChild.SourceEndLine;
        }
    }

    private static bool EndsWithBlankLine(MarkdownNode block) =>
        block.Next is not null && block.SourceEndLine != block.Next.SourceStartLine - 1;

    private void FinalizeCodeBlock(MarkdownNode block)
    {
        if (block.IsFenced)
        {
            string content = block.StringContent;
            int newline = content.IndexOf('\n');
            string firstLine = newline < 0 ? content : content[..newline];
            block.Info = StringUtil.Unescape(firstLine).Trim();
            block.Literal = newline < 0 ? string.Empty : content[(newline + 1)..];
        }
        else
        {
            string[] lines = block.StringContent.Split('\n');
            int last = lines.Length - 1;
            while (last >= 0 && StringUtil.IsBlank(lines[last]))
            {
                last--;
            }

            block.Literal = string.Join('\n', lines, 0, last + 1) + "\n";
            block.SourceEndLine = block.SourceStartLine + last;
        }

        block.StringContent = string.Empty;
    }

    // ------------------------------------------------------------------
    // Container continuation rules
    // ------------------------------------------------------------------

    /// <summary>0 = matched, 1 = not matched, 2 = line consumed.</summary>
    private int Continue(MarkdownNode container)
    {
        switch (container.Type)
        {
            case NodeType.Document:
            case NodeType.List:
            case NodeType.Table:
                if (container.Type == NodeType.Table && _blank)
                {
                    return 1; // a table ends at the first blank line
                }

                return 0;

            case NodeType.BlockQuote:
                if (!_indented && Peek(_currentLine, _nextNonspace) == '>')
                {
                    AdvanceNextNonspace();
                    AdvanceOffset(1, columns: false);
                    if (IsSpaceOrTab(Peek(_currentLine, _offset)))
                    {
                        AdvanceOffset(1, columns: true);
                    }

                    return 0;
                }

                return 1;

            case NodeType.Item:
                ListData itemData = container.ListData!;
                if (_indent >= itemData.MarkerOffset + itemData.Padding)
                {
                    AdvanceOffset(itemData.MarkerOffset + itemData.Padding, columns: true);
                }
                else if (_blank && container.FirstChild is not null)
                {
                    // Blank lines continue an item even without the required indentation.
                    AdvanceNextNonspace();
                }
                else
                {
                    return 1;
                }

                return 0;

            case NodeType.Heading:
            case NodeType.ThematicBreak:
            case NodeType.TableRow:
            case NodeType.TableCell:
                return 1;

            case NodeType.CodeBlock:
                return ContinueCodeBlock(container);

            case NodeType.HtmlBlock:
                return _blank && container.HtmlBlockType is 6 or 7 ? 1 : 0;

            case NodeType.Paragraph:
                return _blank ? 1 : 0;

            default:
                return 1;
        }
    }

    private int ContinueCodeBlock(MarkdownNode container)
    {
        if (container.IsFenced)
        {
            Match match = _indent <= 3 && Peek(_currentLine, _nextNonspace) == container.FenceChar
                ? ClosingCodeFence.Match(_currentLine[_nextNonspace..])
                : Match.Empty;

            if (match.Success && match.Length >= container.FenceLength)
            {
                _lastLineLength = _offset + _indent + match.Length;
                Finalize(container, _lineNumber);
                return 2;
            }

            // A fenced block may indent its content by the same amount as its
            // opening fence; that indentation is not part of the code.
            int spaces = container.FenceOffset;
            while (spaces > 0 && IsSpaceOrTab(Peek(_currentLine, _offset)))
            {
                AdvanceOffset(1, columns: true);
                spaces--;
            }

            return 0;
        }

        if (_indent >= CodeIndent)
        {
            AdvanceOffset(CodeIndent, columns: true);
            return 0;
        }

        if (_blank)
        {
            AdvanceNextNonspace();
            return 0;
        }

        return 1;
    }

    private static bool CanContain(NodeType parent, NodeType child) => parent switch
    {
        NodeType.Document or NodeType.BlockQuote or NodeType.Item => child != NodeType.Item,
        NodeType.List => child == NodeType.Item,
        NodeType.Table => child == NodeType.TableRow,
        NodeType.TableRow => child == NodeType.TableCell,
        _ => false
    };

    private static bool AcceptsLines(NodeType type) =>
        type is NodeType.Paragraph or NodeType.CodeBlock or NodeType.HtmlBlock;

    // ------------------------------------------------------------------
    // Block starts
    // ------------------------------------------------------------------

    private int TryStartBlockQuote(MarkdownNode container)
    {
        if (_indented || Peek(_currentLine, _nextNonspace) != '>')
        {
            return 0;
        }

        AdvanceNextNonspace();
        AdvanceOffset(1, columns: false);
        if (IsSpaceOrTab(Peek(_currentLine, _offset)))
        {
            AdvanceOffset(1, columns: true);
        }

        CloseUnmatchedBlocks();
        AddChild(NodeType.BlockQuote, _nextNonspace);
        return 1;
    }

    private int TryStartAtxHeading(MarkdownNode container)
    {
        if (_indented)
        {
            return 0;
        }

        Match match = AtxHeadingMarker.Match(_currentLine[_nextNonspace..]);
        if (!match.Success)
        {
            return 0;
        }

        AdvanceNextNonspace();
        AdvanceOffset(match.Length, columns: false);
        CloseUnmatchedBlocks();

        MarkdownNode heading = AddChild(NodeType.Heading, _nextNonspace);
        heading.HeadingLevel = match.Value.Trim().Length;

        string content = _currentLine[_offset..];
        content = OnlyHashSequence.Replace(content, string.Empty);
        content = ClosingHashSequence.Replace(content, string.Empty);
        heading.StringContent = content;

        AdvanceOffset(_currentLine.Length - _offset, columns: false);
        return 2;
    }

    private int TryStartFencedCode(MarkdownNode container)
    {
        if (_indented)
        {
            return 0;
        }

        Match match = CodeFence.Match(_currentLine[_nextNonspace..]);
        if (!match.Success)
        {
            return 0;
        }

        int fenceLength = match.Length;
        CloseUnmatchedBlocks();

        MarkdownNode code = AddChild(NodeType.CodeBlock, _nextNonspace);
        code.IsFenced = true;
        code.FenceLength = fenceLength;
        code.FenceChar = match.Value[0];
        code.FenceOffset = _indent;

        AdvanceNextNonspace();
        AdvanceOffset(fenceLength, columns: false);
        return 2;
    }

    private int TryStartHtmlBlock(MarkdownNode container)
    {
        if (_indented || Peek(_currentLine, _nextNonspace) != '<')
        {
            return 0;
        }

        string rest = _currentLine[_nextNonspace..];
        for (int type = 1; type <= 7; type++)
        {
            bool matches = type switch
            {
                1 => HtmlBlockOpen1.IsMatch(rest),
                2 => rest.StartsWith("<!--", StringComparison.Ordinal),
                3 => rest.StartsWith("<?", StringComparison.Ordinal),
                4 => rest.Length >= 3 && rest[0] == '<' && rest[1] == '!' && char.IsAsciiLetter(rest[2]),
                5 => rest.StartsWith("<![CDATA[", StringComparison.Ordinal),
                6 => HtmlBlockOpen6.IsMatch(rest),
                7 => HtmlBlockOpen7.IsMatch(rest) &&
                     container.Type != NodeType.Paragraph &&
                     !(!_allClosed && !_blank && _tip!.Type == NodeType.Paragraph),
                _ => false
            };

            if (!matches)
            {
                continue;
            }

            CloseUnmatchedBlocks();

            // The indentation before an HTML block is part of its content, so
            // unlike other block starts the offset is intentionally not advanced.
            MarkdownNode html = AddChild(NodeType.HtmlBlock, _offset);
            html.HtmlBlockType = type;
            return 2;
        }

        return 0;
    }

    private int TryStartSetextHeading(MarkdownNode container)
    {
        if (_indented || container.Type != NodeType.Paragraph)
        {
            return 0;
        }

        Match match = SetextHeadingLine.Match(_currentLine[_nextNonspace..]);
        if (!match.Success)
        {
            return 0;
        }

        CloseUnmatchedBlocks();

        // A paragraph may contain link reference definitions followed by the
        // actual heading text. Those definitions still have to enter the reference
        // map, so they stay attached to a paragraph node before the heading.
        var scratchReferences = new Dictionary<string, LinkReference>(StringComparer.Ordinal);
        int refDefLength = 0;
        int consumed;
        while (refDefLength < container.StringContent.Length &&
               container.StringContent[refDefLength] == '[' &&
               (consumed = _inlineParser.ParseReference(
                   container.StringContent[refDefLength..], scratchReferences)) > 0)
        {
            refDefLength += consumed;
        }

        if (container.StringContent.Length <= refDefLength)
        {
            return 0;
        }

        string refDefText = container.StringContent[..refDefLength];
        int refDefLines = refDefText.Count(c => c == '\n');

        var heading = new MarkdownNode(NodeType.Heading)
        {
            HeadingLevel = match.Value[0] == '=' ? 1 : 2,
            StringContent = container.StringContent[refDefLength..],
            SourceStartLine = container.SourceStartLine + refDefLines,
            SourceEndLine = _lineNumber,
            IsOpen = false
        };

        container.InsertAfter(heading);

        if (refDefLength > 0)
        {
            container.StringContent = refDefText;
            container.IsOpen = false;
            container.SourceEndLine = container.SourceStartLine + refDefLines - 1;
        }
        else
        {
            container.Unlink();
        }

        _tip = heading;
        AdvanceOffset(_currentLine.Length - _offset, columns: false);
        return 2;
    }

    private int TryStartThematicBreak(MarkdownNode container)
    {
        if (_indented || !ThematicBreak.IsMatch(_currentLine[_nextNonspace..]))
        {
            return 0;
        }

        CloseUnmatchedBlocks();
        AddChild(NodeType.ThematicBreak, _nextNonspace);
        AdvanceOffset(_currentLine.Length - _offset, columns: false);
        return 2;
    }

    private int TryStartListItem(MarkdownNode container)
    {
        ListData? data = null;
        if (!_indented || container.Type == NodeType.List)
        {
            data = ParseListMarker(container);
        }

        if (data is null)
        {
            return 0;
        }

        CloseUnmatchedBlocks();

        if (_tip!.Type != NodeType.List ||
            container.ListData is null ||
            !ListsMatch(container.ListData, data))
        {
            MarkdownNode list = AddChild(NodeType.List, _nextNonspace);
            list.ListData = data;
        }

        MarkdownNode item = AddChild(NodeType.Item, _nextNonspace);
        item.ListData = data;
        return 1;
    }

    private int TryStartIndentedCode(MarkdownNode container)
    {
        if (!_indented || _tip!.Type == NodeType.Paragraph || _blank)
        {
            return 0;
        }

        AdvanceOffset(CodeIndent, columns: true);
        CloseUnmatchedBlocks();
        MarkdownNode code = AddChild(NodeType.CodeBlock, _offset);
        code.IsFenced = false;
        return 2;
    }

    private ListData? ParseListMarker(MarkdownNode container)
    {
        if (_indent >= 4)
        {
            return null;
        }

        string rest = _currentLine[_nextNonspace..];
        Match match;
        char? bullet = null;
        int? start = null;
        char? delimiter = null;
        ListType type;

        Match bulletMatch = BulletListMarker.Match(rest);
        if (bulletMatch.Success)
        {
            type = ListType.Bullet;
            bullet = bulletMatch.Value[0];
            match = bulletMatch;
        }
        else
        {
            Match orderedMatch = OrderedListMarker.Match(rest);
            if (!orderedMatch.Success || (container.Type == NodeType.Paragraph && orderedMatch.Groups[1].Value != "1"))
            {
                return null;
            }

            type = ListType.Ordered;
            start = int.Parse(orderedMatch.Groups[1].Value);
            delimiter = orderedMatch.Groups[2].Value[0];
            match = orderedMatch;
        }

        int afterMarker = _nextNonspace + match.Length;
        int nextChar = Peek(_currentLine, afterMarker);
        if (nextChar is not (-1 or ' ' or '\t'))
        {
            return null;
        }

        // A list item can interrupt a paragraph only if the marker is not followed
        // by a blank line.
        if (container.Type == NodeType.Paragraph &&
            !NonSpace.IsMatch(_currentLine[afterMarker..]))
        {
            return null;
        }

        AdvanceNextNonspace();
        AdvanceOffset(match.Length, columns: true);

        int spacesStartColumn = _column;
        int spacesStartOffset = _offset;
        do
        {
            AdvanceOffset(1, columns: true);
            nextChar = Peek(_currentLine, _offset);
        }
        while (_column - spacesStartColumn < 5 && IsSpaceOrTab(nextChar));

        bool blankItem = Peek(_currentLine, _offset) == -1;
        int spacesAfterMarker = _column - spacesStartColumn;
        int padding;

        if (spacesAfterMarker >= 5 || spacesAfterMarker < 1 || blankItem)
        {
            padding = match.Length + 1;
            _column = spacesStartColumn;
            _offset = spacesStartOffset;
            if (IsSpaceOrTab(Peek(_currentLine, _offset)))
            {
                AdvanceOffset(1, columns: true);
            }
        }
        else
        {
            padding = match.Length + spacesAfterMarker;
        }

        return new ListData
        {
            Type = type,
            BulletChar = bullet,
            Start = start,
            Delimiter = delimiter,
            MarkerOffset = _indent,
            Padding = padding
        };
    }

    private static bool ListsMatch(ListData a, ListData b) =>
        a.Type == b.Type && a.Delimiter == b.Delimiter && a.BulletChar == b.BulletChar;

    // ------------------------------------------------------------------
    // GitHub tables
    // ------------------------------------------------------------------

    private int TryStartTableHeader(MarkdownNode container)
    {
        if (!_options.Tables || _indented || container.Type != NodeType.Paragraph)
        {
            return 0;
        }

        if (!TableParser.TryParseDelimiterRow(_currentLine[_nextNonspace..], out TableAlignment[] alignments))
        {
            return 0;
        }

        if (!TableParser.TrySplitHeader(container.StringContent, out List<string> headerCells,
                out string precedingText) ||
            headerCells.Count != alignments.Length)
        {
            return 0;
        }

        CloseUnmatchedBlocks();

        int tableStartLine = container.SourceStartLine;
        MarkdownNode? parent = container.Parent;
        if (precedingText.Length > 0)
        {
            container.StringContent = precedingText;
            container.SourceEndLine = _lineNumber - 1;
            container.IsOpen = false;
            _tip = parent;
        }
        else
        {
            container.Unlink();
            _tip = parent;
        }

        MarkdownNode table = AddChild(NodeType.Table, _nextNonspace);
        table.SourceStartLine = Math.Max(tableStartLine, _lineNumber - 1);
        table.TableAlignments = alignments;
        AppendTableRow(table, headerCells, isHeader: true);

        // The delimiter line has been consumed as part of opening the table.
        AdvanceOffset(_currentLine.Length - _offset, columns: false);
        return 2;
    }

    private int TryStartTableRow(MarkdownNode container)
    {
        if (!_options.Tables || _indented || container.Type != NodeType.Table)
        {
            return 0;
        }

        List<string> cells = TableParser.SplitRow(_currentLine[_nextNonspace..]);
        if (cells.Count == 0)
        {
            return 0;
        }

        AppendTableRow(container, cells, isHeader: false);

        // Consume the row line; the table itself remains the open container.
        AdvanceOffset(_currentLine.Length - _offset, columns: false);
        return 2;
    }

    private void AppendTableRow(MarkdownNode table, List<string> cells, bool isHeader)
    {
        TableAlignment[] alignments = table.TableAlignments!;
        var row = new MarkdownNode(NodeType.TableRow)
        {
            IsTableHeader = isHeader,
            SourceStartLine = isHeader ? Math.Max(1, _lineNumber - 1) : _lineNumber,
            SourceEndLine = isHeader ? Math.Max(1, _lineNumber - 1) : _lineNumber,
            IsOpen = false
        };
        table.AppendChild(row);

        for (int i = 0; i < alignments.Length; i++)
        {
            var cell = new MarkdownNode(NodeType.TableCell)
            {
                StringContent = i < cells.Count ? cells[i] : string.Empty,
                CellAlignment = alignments[i],
                IsOpen = false,
                SourceStartLine = row.SourceStartLine,
                SourceEndLine = row.SourceEndLine
            };
            row.AppendChild(cell);
        }
    }

    // ------------------------------------------------------------------
    // Inline processing and reference definitions
    // ------------------------------------------------------------------

    private void ProcessInlines(MarkdownNode node)
    {
        if (node.Type is NodeType.Paragraph or NodeType.Heading or NodeType.TableCell)
        {
            _inlineParser.Parse(node);
            return;
        }

        foreach (MarkdownNode child in node.Children)
        {
            ProcessInlines(child);
        }
    }

    private void RemoveLinkReferenceDefinitions(MarkdownNode root)
    {
        // Collect first: unlinking a paragraph detaches its sibling pointers, so a
        // live iterator over the tree would stop early on consecutive definitions.
        foreach (MarkdownNode node in Descendants(root).ToList())
        {
            if (node.Type != NodeType.Paragraph)
            {
                continue;
            }

            bool hasDefinitions = false;
            int removedLength = 0;
            int consumed;
            while (removedLength < node.StringContent.Length &&
                   node.StringContent[removedLength] == '[' &&
                   (consumed = _inlineParser.ParseReference(
                       node.StringContent[removedLength..], _references)) > 0)
            {
                removedLength += consumed;
                hasDefinitions = true;
            }

            if (!hasDefinitions)
            {
                continue;
            }

            string removedText = node.StringContent[..removedLength];
            node.StringContent = node.StringContent[removedLength..];
            node.SourceStartLine += removedText.Count(c => c == '\n') - 1;

            if (StringUtil.IsBlank(node.StringContent))
            {
                node.Unlink();
            }
        }
    }

    private static IEnumerable<MarkdownNode> Descendants(MarkdownNode root)
    {
        foreach (MarkdownNode child in root.Children)
        {
            yield return child;
            foreach (MarkdownNode descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    // ------------------------------------------------------------------
    // Low-level cursor helpers
    // ------------------------------------------------------------------

    private void AdvanceOffset(int count, bool columns)
    {
        while (count > 0 && _offset < _currentLine.Length)
        {
            char c = _currentLine[_offset];
            if (c == '\t')
            {
                int charsToTab = 4 - (_column % 4);
                if (columns)
                {
                    _partiallyConsumedTab = charsToTab > count;
                    int charsToAdvance = charsToTab > count ? count : charsToTab;
                    _column += charsToAdvance;
                    _offset += _partiallyConsumedTab ? 0 : 1;
                    count -= charsToAdvance;
                }
                else
                {
                    _partiallyConsumedTab = false;
                    _column += charsToTab;
                    _offset += 1;
                    count -= 1;
                }
            }
            else
            {
                _partiallyConsumedTab = false;
                _offset += 1;
                _column += 1;
                count -= 1;
            }
        }
    }

    private void AdvanceNextNonspace()
    {
        _offset = _nextNonspace;
        _column = _nextNonspaceColumn;
        _partiallyConsumedTab = false;
    }

    private void FindNextNonspace()
    {
        int i = _offset;
        int columns = _column;
        int c;

        while ((c = Peek(_currentLine, i)) != -1)
        {
            if (c == ' ')
            {
                i++;
                columns++;
            }
            else if (c == '\t')
            {
                i++;
                columns += 4 - (columns % 4);
            }
            else
            {
                break;
            }
        }

        _blank = c is -1 or '\n' or '\r';
        _nextNonspace = i;
        _nextNonspaceColumn = columns;
        _indent = _nextNonspaceColumn - _column;
        _indented = _indent >= CodeIndent;
    }

    private static bool IsSpaceOrTab(int c) => c is ' ' or '\t';

    private static int Peek(string line, int position) =>
        position >= 0 && position < line.Length ? line[position] : -1;

    private static bool HtmlBlockEnds(int type, string line) => type switch
    {
        1 => HtmlBlockClose1.IsMatch(line),
        2 => HtmlBlockClose2.IsMatch(line),
        3 => HtmlBlockClose3.IsMatch(line),
        4 => HtmlBlockClose4.IsMatch(line),
        5 => HtmlBlockClose5.IsMatch(line),
        _ => false
    };
}

/// <summary>
/// The raw HTML tag grammar shared by inline HTML and HTML block type 7.
/// </summary>
internal static class HtmlTag
{
    private const string TagName = "[A-Za-z][A-Za-z0-9-]*";
    private const string AttributeName = "[a-zA-Z_:][a-zA-Z0-9:._-]*";
    private const string UnquotedValue = @"[^""'=<>`\x00-\x20]+";
    private const string SingleQuotedValue = "'[^']*'";
    private const string DoubleQuotedValue = "\"[^\"]*\"";
    private const string AttributeValue =
        "(?:" + UnquotedValue + "|" + SingleQuotedValue + "|" + DoubleQuotedValue + ")";
    private const string Attribute =
        @"(?:\s+" + AttributeName + @"(?:\s*=\s*" + AttributeValue + ")?)";

    public const string OpenTag = "<" + TagName + Attribute + @"*\s*/?>";
    public const string CloseTag = "</" + TagName + @"\s*>";

    private const string Declaration = "<![A-Za-z]+[^>]*>";
    private const string CData = @"<!\[CDATA\[[\s\S]*?\]\]>";
    private const string Comment = @"<!-->|<!--->|<!--[\s\S]*?-->";
    private const string ProcessingInstruction = @"[<][?][\s\S]*?[?][>]";

    public static readonly Regex Inline = new(
        "^(?:" + OpenTag + "|" + CloseTag + "|" + Comment + "|" +
        ProcessingInstruction + "|" + Declaration + "|" + CData + ")",
        RegexOptions.Compiled);
}
