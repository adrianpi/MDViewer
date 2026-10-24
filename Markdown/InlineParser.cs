using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Md2Html.Markdown;

/// <summary>
/// Phase 2 of CommonMark parsing: code spans, emphasis, links/images, entities,
/// raw HTML, autolinks, and line breaks. Emphasis uses the delimiter-stack
/// algorithm from the specification appendix.
/// </summary>
internal sealed class InlineParser
{
    private const string EscapableClass = "[" + "!\"#$%&'()*+,\\-./:;<=>?@\\[\\\\\\]^_`{|}~" + "]";

    private static readonly Regex LinkTitlePattern = BuildLinkTitleRegex();
    private static readonly Regex LinkDestinationBraces = new(@"^<(?:[^<>\n\\\x00]|\\.)*>", RegexOptions.Compiled);
    private static readonly Regex LinkLabelPattern = new(
        @"^\[(?:[^\\\[\]]|\\.){0,1000}\]",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex EmailAutolinkPattern = new(
        @"^<([a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*)>",
        RegexOptions.Compiled);
    private static readonly Regex AutolinkPattern = new(
        @"^<[A-Za-z][A-Za-z0-9.+-]{1,31}:[^<>\x00-\x20]*>",
        RegexOptions.Compiled);
    private static readonly Regex SpnlPattern = new(@"^ *(?:\n *)?", RegexOptions.Compiled);
    private static readonly Regex InitialSpacePattern = new(@"^ *", RegexOptions.Compiled);
    private static readonly Regex FinalSpacePattern = new(@" *$", RegexOptions.Compiled);
    private static readonly Regex SpaceAtEndOfLinePattern = new(@"^ *(?:\n|$)", RegexOptions.Compiled);

    private readonly MarkdownOptions _options;

    private string _subject = string.Empty;

    public InlineParser(MarkdownOptions options)
    {
        _options = options;
    }

    public Dictionary<string, LinkReference> References { get; set; } = new(StringComparer.Ordinal);

    private int Position { get; set; }
    private Delimiter? Delimiters { get; set; }
    private Bracket? Brackets { get; set; }

    // ------------------------------------------------------------------
    // Entry points
    // ------------------------------------------------------------------

    public void Parse(MarkdownNode block)
    {
        _subject = StringUtil.TrimAscii(block.StringContent);
        Position = 0;
        Delimiters = null;
        Brackets = null;

        while (ParseInline(block))
        {
        }

        block.StringContent = string.Empty;
        ProcessEmphasis(null);
    }

    /// <summary>
    /// Parses a link reference definition at the start of <paramref name="text"/>.
    /// Returns the number of characters consumed, or zero if there is none.
    /// Definitions are added to <paramref name="references"/> in document order.
    /// </summary>
    public int ParseReference(string text, Dictionary<string, LinkReference> references)
    {
        _subject = text;
        Position = 0;
        int start = Position;

        int labelLength = ParseLinkLabel();
        if (labelLength == 0)
        {
            return 0;
        }

        string rawLabel = _subject[..labelLength];
        if (Peek() != ':')
        {
            Position = start;
            return 0;
        }

        Position++;

        Spnl();
        string? destination = ParseLinkDestination();
        if (destination is null)
        {
            Position = start;
            return 0;
        }

        int beforeTitle = Position;
        Spnl();
        string? title = null;
        if (Position != beforeTitle)
        {
            title = ParseLinkTitle();
        }

        if (title is null)
        {
            Position = beforeTitle;
        }

        bool atLineEnd = true;
        if (!MatchAt(SpaceAtEndOfLinePattern))
        {
            if (title is null)
            {
                atLineEnd = false;
            }
            else
            {
                // The candidate title did not reach the end of the line. It may
                // still be a valid definition without a title.
                title = null;
                Position = beforeTitle;
                atLineEnd = MatchAt(SpaceAtEndOfLinePattern);
            }
        }

        if (!atLineEnd)
        {
            Position = start;
            return 0;
        }

        string normalized = StringUtil.NormalizeReference(rawLabel);
        if (normalized.Length == 0)
        {
            Position = start;
            return 0;
        }

        if (!references.ContainsKey(normalized))
        {
            references[normalized] = new LinkReference(destination, title ?? string.Empty);
        }

        return Position - start;
    }

    // ------------------------------------------------------------------
    // Inline dispatch
    // ------------------------------------------------------------------

    private bool ParseInline(MarkdownNode block)
    {
        int c = Peek();
        if (c == -1)
        {
            return false;
        }

        bool parsed = c switch
        {
            '\n' => ParseNewline(block),
            '\\' => ParseBackslash(block),
            '`' => ParseBackticks(block),
            '*' or '_' => HandleDelimiter((char)c, block),
            '~' when _options.Strikethrough => HandleDelimiter('~', block),
            '[' => ParseOpenBracket(block),
            '!' => ParseBang(block),
            ']' => ParseCloseBracket(block),
            '<' => ParseAutolink(block) || ParseHtmlTag(block),
            '&' => ParseEntity(block),
            'w' when _options.AutolinkLiterals => TryParseWwwAutolink(block),
            ':' when _options.AutolinkLiterals => TryParseUrlAutolink(block),
            _ => ParseString(block)
        };

        if (!parsed)
        {
            int length = char.IsSurrogatePair(_subject, Position) ? 2 : 1;
            block.AppendChild(Text(_subject.Substring(Position, length)));
            Position += length;
        }

        return true;
    }

    private bool ParseString(MarkdownNode block)
    {
        int start = Position;
        while (Position < _subject.Length)
        {
            char c = _subject[Position];
            if (IsTextSpecial(c))
            {
                break;
            }

            if (_options.AutolinkLiterals)
            {
                if (c == 'w' && IsWwwAutolinkCandidate(Position))
                {
                    break;
                }

                if (c == ':' && LooksLikeUrlScheme(Position))
                {
                    break;
                }
            }

            Position++;
        }

        if (Position == start)
        {
            return false;
        }

        block.AppendChild(Text(_subject[start..Position]));
        return true;
    }

    private bool IsTextSpecial(char c) =>
        c is '\n' or '`' or '[' or ']' or '\\' or '!' or '<' or '&' or '*' or '_' ||
        (c == '~' && _options.Strikethrough);

    private bool ParseBackticks(MarkdownNode block)
    {
        int runStart = Position;
        int ticks = CountRun('`');
        int afterOpenTicks = Position;

        while (Position < _subject.Length)
        {
            int next = _subject.IndexOf('`', Position);
            if (next < 0)
            {
                break;
            }

            Position = next;
            int runLength = CountRun('`');
            if (runLength != ticks)
            {
                continue;
            }

            int contentStart = afterOpenTicks;
            int contentEnd = next;
            string contents = _subject[contentStart..contentEnd].Replace('\n', ' ');
            if (contents.Length > 0 &&
                contents.Trim(' ').Length > 0 &&
                contents[0] == ' ' &&
                contents[^1] == ' ')
            {
                contents = contents[1..^1];
            }

            block.AppendChild(new MarkdownNode(NodeType.Code) { Literal = contents });
            return true;
        }

        // No matching run: the opening backticks are literal text.
        Position = afterOpenTicks;
        block.AppendChild(Text(_subject[runStart..afterOpenTicks]));
        return true;
    }

    private bool ParseBackslash(MarkdownNode block)
    {
        Position++;
        if (Peek() == '\n')
        {
            Position++;
            block.AppendChild(new MarkdownNode(NodeType.LineBreak));
        }
        else if (Position < _subject.Length && StringUtil.IsAsciiPunctuation(_subject[Position]))
        {
            block.AppendChild(Text(_subject[Position].ToString()));
            Position++;
        }
        else
        {
            block.AppendChild(Text("\\"));
        }

        return true;
    }

    private bool ParseAutolink(MarkdownNode block)
    {
        Match email = EmailAutolinkPattern.Match(_subject[Position..]);
        if (email.Success)
        {
            string address = StringUtil.DecodeEntities(
                email.Value[1..^1]);
            Position += email.Length;
            var link = new MarkdownNode(NodeType.Link)
            {
                Destination = StringUtil.NormalizeUri("mailto:" + address),
                Title = string.Empty
            };
            link.AppendChild(Text(address));
            block.AppendChild(link);
            return true;
        }

        Match url = AutolinkPattern.Match(_subject[Position..]);
        if (url.Success)
        {
            string destination = StringUtil.DecodeEntities(url.Value[1..^1]);
            Position += url.Length;
            var link = new MarkdownNode(NodeType.Link)
            {
                Destination = StringUtil.NormalizeUri(destination),
                Title = string.Empty
            };
            link.AppendChild(Text(destination));
            block.AppendChild(link);
            return true;
        }

        return false;
    }

    private bool ParseHtmlTag(MarkdownNode block)
    {
        Match match = HtmlTag.Inline.Match(_subject[Position..]);
        if (!match.Success)
        {
            return false;
        }

        Position += match.Length;
        if (_options.AllowRawHtml)
        {
            block.AppendChild(new MarkdownNode(NodeType.HtmlInline) { Literal = match.Value });
        }
        else
        {
            block.AppendChild(Text(match.Value));
        }

        return true;
    }

    private bool ParseEntity(MarkdownNode block)
    {
        if (!HtmlEntities.TryDecodeAt(_subject, Position, out string decoded, out int length))
        {
            return false;
        }

        Position += length;
        block.AppendChild(Text(decoded));
        return true;
    }

    private bool ParseNewline(MarkdownNode block)
    {
        Position++;

        MarkdownNode? last = block.LastChild;
        if (last is { Type: NodeType.Text } && last.Literal.EndsWith(' '))
        {
            bool hardBreak = last.Literal.Length >= 2 && last.Literal[^2] == ' ';
            last.Literal = FinalSpacePattern.Replace(last.Literal, string.Empty);
            block.AppendChild(new MarkdownNode(hardBreak ? NodeType.LineBreak : NodeType.SoftBreak));
        }
        else
        {
            block.AppendChild(new MarkdownNode(NodeType.SoftBreak));
        }

        MatchAt(InitialSpacePattern);
        return true;
    }

    // ------------------------------------------------------------------
    // Emphasis
    // ------------------------------------------------------------------

    private bool HandleDelimiter(char character, MarkdownNode block)
    {
        DelimiterInfo? scanned = ScanDelimiters(character);
        if (scanned is null)
        {
            return false;
        }

        DelimiterInfo info = scanned.Value;
        int start = Position;
        string literal = _subject.Substring(start, info.Count);
        Position += info.Count;

        MarkdownNode node = Text(literal);
        block.AppendChild(node);

        // GFM strikethrough is exactly two tildes; runs of another length are
        // literal text and must not participate in emphasis matching.
        bool participates = character != '~' || info.Count == 2;

        if (participates && (info.CanOpen || info.CanClose))
        {
            var delimiter = new Delimiter
            {
                Character = character,
                Node = node,
                CanOpen = info.CanOpen,
                CanClose = info.CanClose,
                OriginalLength = info.Count,
                Length = info.Count,
                Previous = Delimiters
            };

            if (Delimiters is not null)
            {
                Delimiters.Next = delimiter;
            }

            Delimiters = delimiter;
        }

        return true;
    }

    private DelimiterInfo? ScanDelimiters(char character)
    {
        int start = Position;
        int count = 0;
        while (Peek() == character)
        {
            Position += char.IsSurrogatePair(_subject, Position) ? 2 : 1;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        int before = PreviousCodePoint(start);
        int after = Peek();

        bool afterIsWhitespace = StringUtil.IsUnicodeWhitespace(after < 0 ? '\n' : after);
        bool afterIsPunctuation = StringUtil.IsUnicodePunctuation(after < 0 ? '\n' : after);
        bool beforeIsWhitespace = StringUtil.IsUnicodeWhitespace(before < 0 ? '\n' : before);
        bool beforeIsPunctuation = StringUtil.IsUnicodePunctuation(before < 0 ? '\n' : before);

        bool leftFlanking = !afterIsWhitespace &&
                            (!afterIsPunctuation || beforeIsWhitespace || beforeIsPunctuation);
        bool rightFlanking = !beforeIsWhitespace &&
                             (!beforeIsPunctuation || afterIsWhitespace || afterIsPunctuation);

        bool canOpen;
        bool canClose;
        if (character == '_')
        {
            canOpen = leftFlanking && (!rightFlanking || beforeIsPunctuation);
            canClose = rightFlanking && (!leftFlanking || afterIsPunctuation);
        }
        else
        {
            canOpen = leftFlanking;
            canClose = rightFlanking;
        }

        Position = start;
        return new DelimiterInfo(count, canOpen, canClose);
    }

    private void ProcessEmphasis(Delimiter? stackBottom)
    {
        var openersBottom = new Delimiter?[16];
        for (int i = 0; i < openersBottom.Length; i++)
        {
            openersBottom[i] = stackBottom;
        }

        // Find the first delimiter above the stack bottom.
        Delimiter? closer = Delimiters;
        while (closer is not null && closer.Previous != stackBottom)
        {
            closer = closer.Previous;
        }

        while (closer is not null)
        {
            if (!closer.CanClose)
            {
                closer = closer.Next;
                continue;
            }

            int bottomIndex = closer.Character switch
            {
                '_' => 2 + (closer.CanOpen ? 3 : 0) + closer.OriginalLength % 3,
                '*' => 8 + (closer.CanOpen ? 3 : 0) + closer.OriginalLength % 3,
                '~' => 14,
                _ => 15
            };

            Delimiter? opener = closer.Previous;
            bool openerFound = false;
            while (opener is not null && opener != stackBottom && opener != openersBottom[bottomIndex])
            {
                bool oddMatch = closer.Character != '~' &&
                                (closer.CanOpen || opener.CanClose) &&
                                closer.OriginalLength % 3 != 0 &&
                                (opener.OriginalLength + closer.OriginalLength) % 3 == 0;

                if (opener.Character == closer.Character && opener.CanOpen && !oddMatch)
                {
                    openerFound = true;
                    break;
                }

                opener = opener.Previous;
            }

            Delimiter oldCloser = closer;

            if (closer.Character is '*' or '_' or '~')
            {
                if (!openerFound)
                {
                    closer = closer.Next;
                }
                else
                {
                    int useDelimiters = closer.Character == '~'
                        ? 2
                        : closer.Length >= 2 && opener!.Length >= 2 ? 2 : 1;

                    MarkdownNode openerNode = opener!.Node;
                    MarkdownNode closerNode = closer.Node;

                    opener.Length -= useDelimiters;
                    closer.Length -= useDelimiters;
                    openerNode.Literal = openerNode.Literal[..^useDelimiters];
                    closerNode.Literal = closerNode.Literal[..^useDelimiters];

                    NodeType emphasisType = closer.Character == '~'
                        ? NodeType.Strikethrough
                        : useDelimiters == 1 ? NodeType.Emphasis : NodeType.Strong;
                    var emphasis = new MarkdownNode(emphasisType);

                    MarkdownNode? current = openerNode.Next;
                    while (current is not null && current != closerNode)
                    {
                        MarkdownNode next = current.Next!;
                        current.Unlink();
                        emphasis.AppendChild(current);
                        current = next;
                    }

                    openerNode.InsertAfter(emphasis);
                    RemoveDelimitersBetween(opener, closer);

                    if (opener.Length == 0)
                    {
                        openerNode.Unlink();
                        RemoveDelimiter(opener);
                    }

                    if (closer.Length == 0)
                    {
                        closerNode.Unlink();
                        Delimiter? next = closer.Next;
                        RemoveDelimiter(closer);
                        closer = next;
                    }
                }
            }

            if (!openerFound)
            {
                openersBottom[bottomIndex] = oldCloser.Previous;
                if (!oldCloser.CanOpen)
                {
                    RemoveDelimiter(oldCloser);
                }
            }
        }

        while (Delimiters is not null && Delimiters != stackBottom)
        {
            RemoveDelimiter(Delimiters);
        }
    }

    private void RemoveDelimiter(Delimiter delimiter)
    {
        if (delimiter.Previous is not null)
        {
            delimiter.Previous.Next = delimiter.Next;
        }

        if (delimiter.Next is null)
        {
            Delimiters = delimiter.Previous;
        }
        else
        {
            delimiter.Next.Previous = delimiter.Previous;
        }
    }

    private static void RemoveDelimitersBetween(Delimiter bottom, Delimiter top)
    {
        if (bottom.Next != top)
        {
            bottom.Next = top;
            top.Previous = bottom;
        }
    }

    // ------------------------------------------------------------------
    // Links and images
    // ------------------------------------------------------------------

    private bool ParseOpenBracket(MarkdownNode block)
    {
        int start = Position;
        Position++;
        MarkdownNode node = Text("[");
        block.AppendChild(node);
        AddBracket(node, start, isImage: false);
        return true;
    }

    private bool ParseBang(MarkdownNode block)
    {
        int start = Position;
        Position++;
        if (Peek() == '[')
        {
            Position++;
            MarkdownNode node = Text("![");
            block.AppendChild(node);
            AddBracket(node, start + 1, isImage: true);
        }
        else
        {
            block.AppendChild(Text("!"));
        }

        return true;
    }

    private bool ParseCloseBracket(MarkdownNode block)
    {
        Position++;
        int start = Position;
        Bracket? opener = Brackets;

        if (opener is null)
        {
            block.AppendChild(Text("]"));
            return true;
        }

        if (!opener.IsActive)
        {
            block.AppendChild(Text("]"));
            RemoveBracket();
            return true;
        }

        bool isImage = opener.IsImage;
        bool matched = false;
        string? destination = null;
        string? title = null;
        int savePosition = Position;

        if (Peek() == '(')
        {
            Position++;
            if (Spnl() &&
                (destination = ParseLinkDestination()) is not null &&
                Spnl())
            {
                if (Position > 0 && IsWhitespaceChar(_subject[Position - 1]))
                {
                    title = ParseLinkTitle();
                }

                if (Spnl() && Peek() == ')')
                {
                    Position++;
                    matched = true;
                }
            }

            if (!matched)
            {
                Position = savePosition;
            }
        }

        if (!matched)
        {
            int beforeLabel = Position;
            int labelLength = ParseLinkLabel();
            string? referenceLabel = null;

            if (labelLength > 2)
            {
                referenceLabel = _subject.Substring(beforeLabel, labelLength);
            }
            else if (!opener.HasBracketAfter)
            {
                // An empty or missing second label means the first label is the
                // reference. If the first label contains a bracket, it cannot be
                // a reference label (and HasBracketAfter saves us a lookup).
                referenceLabel = _subject[opener.Index..start];
            }

            if (labelLength == 0)
            {
                Position = savePosition;
            }

            if (referenceLabel is not null)
            {
                string normalized = StringUtil.NormalizeReference(referenceLabel);
                if (References.TryGetValue(normalized, out LinkReference link))
                {
                    destination = link.Destination;
                    title = link.Title;
                    matched = true;
                }
            }
        }

        if (!matched)
        {
            RemoveBracket();
            Position = start;
            block.AppendChild(Text("]"));
            return true;
        }

        var node = new MarkdownNode(isImage ? NodeType.Image : NodeType.Link)
        {
            Destination = destination ?? string.Empty,
            Title = title ?? string.Empty
        };

        MarkdownNode? current = opener.Node.Next;
        while (current is not null)
        {
            MarkdownNode next = current.Next!;
            current.Unlink();
            node.AppendChild(current);
            current = next;
        }

        block.AppendChild(node);
        ProcessEmphasis(opener.PreviousDelimiter);
        RemoveBracket();
        opener.Node.Unlink();

        if (!isImage)
        {
            // Links may not contain other links: deactivate all earlier link
            // openers, but leave image openers usable.
            for (Bracket? bracket = Brackets; bracket is not null; bracket = bracket.Previous)
            {
                if (!bracket.IsImage)
                {
                    bracket.IsActive = false;
                }
            }
        }

        return true;
    }

    private void AddBracket(MarkdownNode node, int index, bool isImage)
    {
        if (Brackets is not null)
        {
            Brackets.HasBracketAfter = true;
        }

        Brackets = new Bracket
        {
            Node = node,
            Index = index,
            IsImage = isImage,
            Previous = Brackets,
            PreviousDelimiter = Delimiters
        };
    }

    private void RemoveBracket()
    {
        Brackets = Brackets?.Previous;
    }

    private string? ParseLinkDestination()
    {
        Match braced = LinkDestinationBraces.Match(_subject[Position..]);
        if (braced.Success)
        {
            Position += braced.Length;
            return StringUtil.NormalizeUri(StringUtil.Unescape(braced.Value[1..^1]));
        }

        if (Peek() == '<')
        {
            return null;
        }

        int save = Position;
        int openParens = 0;
        int c;
        while ((c = Peek()) != -1)
        {
            if (c == '\\' && Position + 1 < _subject.Length &&
                StringUtil.IsAsciiPunctuation(_subject[Position + 1]))
            {
                Position += 2;
            }
            else if (c == '(')
            {
                Position++;
                openParens++;
            }
            else if (c == ')')
            {
                if (openParens < 1)
                {
                    break;
                }

                Position++;
                openParens--;
            }
            else if (IsAsciiSpaceOrControl(c))
            {
                break;
            }
            else
            {
                Position += char.IsSurrogatePair(_subject, Position) ? 2 : 1;
            }
        }

        if (Position == save && c != ')')
        {
            return null;
        }

        if (openParens != 0)
        {
            return null;
        }

        return StringUtil.NormalizeUri(StringUtil.Unescape(_subject[save..Position]));
    }

    private string? ParseLinkTitle()
    {
        Match match = LinkTitlePattern.Match(_subject[Position..]);
        if (!match.Success)
        {
            return null;
        }

        Position += match.Length;
        return StringUtil.Unescape(match.Value[1..^1]);
    }

    private int ParseLinkLabel()
    {
        Match match = LinkLabelPattern.Match(_subject[Position..]);
        if (!match.Success || match.Length > 1001)
        {
            return 0;
        }

        Position += match.Length;
        return match.Length;
    }

    private bool Spnl()
    {
        Match match = SpnlPattern.Match(_subject[Position..]);
        Position += match.Length;
        return true;
    }

    // ------------------------------------------------------------------
    // GitHub autolink literals
    // ------------------------------------------------------------------

    private bool TryParseWwwAutolink(MarkdownNode block)
    {
        if (Brackets is not null || !IsWwwAutolinkCandidate(Position))
        {
            return false;
        }

        int domainLength = CheckDomain(Position, allowShort: false);
        if (domainLength == 0)
        {
            return false;
        }

        int linkEnd = Position + domainLength;
        while (linkEnd < _subject.Length &&
               !char.IsWhiteSpace(_subject[linkEnd]) && _subject[linkEnd] != '<')
        {
            linkEnd++;
        }

        int trimmed = AutolinkDelimiter(Position, linkEnd);
        if (trimmed == 0)
        {
            return false;
        }

        linkEnd = Position + trimmed;
        string text = _subject[Position..linkEnd];
        var link = new MarkdownNode(NodeType.Link)
        {
            Destination = "http://" + text,
            Title = string.Empty
        };
        link.AppendChild(Text(text));
        block.AppendChild(link);
        Position = linkEnd;
        return true;
    }

    private bool TryParseUrlAutolink(MarkdownNode block)
    {
        if (Brackets is not null)
        {
            return false;
        }

        int colon = Position;
        int start = colon;
        while (start > 0 && char.IsAsciiLetter(_subject[start - 1]))
        {
            start--;
        }

        if (start == colon ||
            !(_subject.AsSpan(start, colon - start).Equals("http", StringComparison.OrdinalIgnoreCase) ||
              _subject.AsSpan(start, colon - start).Equals("https", StringComparison.OrdinalIgnoreCase) ||
              _subject.AsSpan(start, colon - start).Equals("ftp", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (colon + 3 > _subject.Length || _subject[colon + 1] != '/' || _subject[colon + 2] != '/')
        {
            return false;
        }

        int domainLength = CheckDomain(colon + 3, allowShort: true);
        if (domainLength == 0)
        {
            return false;
        }

        int linkEnd = colon + 3 + domainLength;
        while (linkEnd < _subject.Length &&
               !char.IsWhiteSpace(_subject[linkEnd]) && _subject[linkEnd] != '<')
        {
            linkEnd++;
        }

        int trimmed = AutolinkDelimiter(colon, linkEnd);
        if (trimmed == 0)
        {
            return false;
        }

        linkEnd = colon + trimmed;

        // The scheme was already emitted as ordinary text by ParseString. Remove
        // it from that text node so the link can own the complete URL.
        int rewind = colon - start;
        if (!UnputText(block, rewind))
        {
            return false;
        }

        string text = _subject[start..linkEnd];
        var link = new MarkdownNode(NodeType.Link)
        {
            Destination = text,
            Title = string.Empty
        };
        link.AppendChild(Text(text));
        block.AppendChild(link);
        Position = linkEnd;
        return true;
    }

    private static bool UnputText(MarkdownNode block, int count)
    {
        MarkdownNode? last = block.LastChild;
        if (last is not { Type: NodeType.Text } || last.Literal.Length < count)
        {
            return false;
        }

        last.Literal = last.Literal[..^count];
        if (last.Literal.Length == 0)
        {
            last.Unlink();
        }

        return true;
    }

    private bool IsWwwAutolinkCandidate(int position)
    {
        if (!_subject.AsSpan(position).StartsWith("www.", StringComparison.Ordinal))
        {
            return false;
        }

        if (position == 0)
        {
            return true;
        }

        char previous = _subject[position - 1];
        return char.IsWhiteSpace(previous) || previous is '*' or '_' or '~' or '(';
    }

    private bool LooksLikeUrlScheme(int colonPosition)
    {
        if (colonPosition + 3 > _subject.Length ||
            _subject[colonPosition + 1] != '/' ||
            _subject[colonPosition + 2] != '/')
        {
            return false;
        }

        int start = colonPosition;
        while (start > 0 && char.IsAsciiLetter(_subject[start - 1]))
        {
            start--;
        }

        return _subject.AsSpan(start, colonPosition - start).Equals("http", StringComparison.OrdinalIgnoreCase) ||
               _subject.AsSpan(start, colonPosition - start).Equals("https", StringComparison.OrdinalIgnoreCase) ||
               _subject.AsSpan(start, colonPosition - start).Equals("ftp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a host name and returns an initial end offset. The caller then
    /// extends the link over the path and trims trailing punctuation, exactly as
    /// cmark-gfm's extension scanner does.
    /// </summary>
    private int CheckDomain(int start, bool allowShort)
    {
        int size = _subject.Length - start;
        int periods = 0;
        int underscoresInPreviousSegment = 0;
        int underscoresInCurrentSegment = 0;
        int i = 1;

        while (i < size - 1)
        {
            int codePoint = CodePointAt(start + i);
            int width = codePoint > 0xFFFF ? 2 : 1;

            if (codePoint == '\\' && i < size - 2)
            {
                i += 2;
                continue;
            }

            if (codePoint == '_')
            {
                underscoresInCurrentSegment++;
            }
            else if (codePoint == '.')
            {
                underscoresInPreviousSegment = underscoresInCurrentSegment;
                underscoresInCurrentSegment = 0;
                periods++;
            }
            else if (codePoint != '-' && !IsValidHostCharacter(codePoint))
            {
                break;
            }

            i += width;
        }

        if (underscoresInPreviousSegment > 0 || underscoresInCurrentSegment > 0)
        {
            // Long URLs with underscores are still accepted, but this limit keeps
            // pathological input from causing quadratic behaviour.
            if (periods <= 10)
            {
                return 0;
            }
        }

        if (allowShort)
        {
            return i;
        }

        return periods > 0 ? i : 0;
    }

    private int AutolinkDelimiter(int start, int end)
    {
        int linkEnd = end - start;
        int closing = 0;
        int opening = 0;

        for (int i = 0; i < linkEnd; i++)
        {
            char c = _subject[start + i];
            if (c == '<')
            {
                linkEnd = i;
                break;
            }

            if (c == '(')
            {
                opening++;
            }
            else if (c == ')')
            {
                closing++;
            }
        }

        while (linkEnd > 0)
        {
            char c = _subject[start + linkEnd - 1];
            switch (c)
            {
                case ')':
                    if (closing <= opening)
                    {
                        return linkEnd;
                    }

                    closing--;
                    linkEnd--;
                    break;

                case '?':
                case '!':
                case '.':
                case ',':
                case ':':
                case '*':
                case '_':
                case '~':
                case '\'':
                case '"':
                    linkEnd--;
                    break;

                case ';':
                {
                    int newEnd = linkEnd - 2;
                    while (newEnd > 0 && char.IsAsciiLetter(_subject[start + newEnd]))
                    {
                        newEnd--;
                    }

                    if (newEnd < linkEnd - 2 && _subject[start + newEnd] == '&')
                    {
                        linkEnd = newEnd;
                    }
                    else
                    {
                        linkEnd--;
                    }

                    break;
                }

                default:
                    return linkEnd;
            }
        }

        return linkEnd;
    }

    private static bool IsValidHostCharacter(int codePoint) =>
        !StringUtil.IsUnicodeWhitespace(codePoint) && !StringUtil.IsUnicodePunctuation(codePoint);

    // ------------------------------------------------------------------
    // Low-level helpers
    // ------------------------------------------------------------------

    private int Peek() => Position < _subject.Length ? CodePointAt(Position) : -1;

    private int CodePointAt(int position) => char.ConvertToUtf32(_subject, position);

    private int PreviousCodePoint(int position)
    {
        if (position <= 0)
        {
            return -1;
        }

        if (char.IsLowSurrogate(_subject[position - 1]) && position >= 2 &&
            char.IsHighSurrogate(_subject[position - 2]))
        {
            return char.ConvertToUtf32(_subject[position - 2], _subject[position - 1]);
        }

        return _subject[position - 1];
    }

    private int CountRun(char c)
    {
        int start = Position;
        while (Position < _subject.Length && _subject[Position] == c)
        {
            Position++;
        }

        return Position - start;
    }

    private bool MatchAt(Regex regex)
    {
        Match match = regex.Match(_subject[Position..]);
        if (!match.Success)
        {
            return false;
        }

        Position += match.Length;
        return true;
    }

    private static bool IsWhitespaceChar(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static bool IsAsciiSpaceOrControl(int c) => c is >= 0 and <= 0x20 or 0x7F;

    private static MarkdownNode Text(string literal) => new(NodeType.Text) { Literal = literal };

    private static Regex BuildLinkTitleRegex()
    {
        const string escaped = @"\\" + EscapableClass;
        string doubleQuoted = "(?:" + escaped + "|\\\\[^\\\\]|[^\\\\\"\\x00])*";
        string singleQuoted = "(?:" + escaped + "|\\\\[^\\\\]|[^\\\\'\\x00])*";
        string parenthesized = "(?:" + escaped + "|\\\\[^\\\\]|[^\\\\()\\x00])*";
        return new Regex(
            "^(?:\"" + doubleQuoted + "\"|'" + singleQuoted + "'|\\(" + parenthesized + "\\))",
            RegexOptions.Compiled);
    }

    private readonly record struct DelimiterInfo(int Count, bool CanOpen, bool CanClose);
}
