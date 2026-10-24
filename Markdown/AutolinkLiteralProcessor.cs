using System;
using System.Collections.Generic;

namespace MDViewer.Markdown;

/// <summary>
/// GFM extended email autolinks. URLs beginning with http(s):// or www. are handled
/// during inline parsing (so emphasis markers inside a URL are not misread); email
/// addresses only need a final pass over literal text nodes.
/// </summary>
internal static class AutolinkLiteralProcessor
{
    public static void Process(MarkdownNode document)
    {
        ConsolidateTextNodes(document);
        ProcessNode(document);
    }

    private static void ConsolidateTextNodes(MarkdownNode node)
    {
        MarkdownNode? child = node.FirstChild;
        while (child is not null)
        {
            MarkdownNode? next = child.Next;
            if (child.Type == NodeType.Text && next is { Type: NodeType.Text })
            {
                child.Literal += next.Literal;
                next.Unlink();
                continue;
            }

            ConsolidateTextNodes(child);
            child = next;
        }
    }

    private static void ProcessNode(MarkdownNode node)
    {
        foreach (MarkdownNode child in node.Children)
        {
            if (child.Type == NodeType.Link)
            {
                continue;
            }

            if (child.Type == NodeType.Text)
            {
                LinkifyEmails(child);
            }
            else
            {
                ProcessNode(child);
            }
        }
    }

    private static void LinkifyEmails(MarkdownNode textNode)
    {
        string text = textNode.Literal;
        var replacement = new List<MarkdownNode>();
        int cursor = 0;
        int search = 0;

        while (search < text.Length)
        {
            int at = text.IndexOf('@', search);
            if (at < 0)
            {
                break;
            }

            int start = at;
            while (start > 0 && IsLocalCharacter(text[start - 1]))
            {
                start--;
            }

            if (start == at)
            {
                search = at + 1;
                continue;
            }

            int end = at + 1;
            int periods = 0;
            bool valid = true;
            while (end < text.Length)
            {
                char c = text[end];
                if (char.IsAsciiLetterOrDigit(c))
                {
                    end++;
                }
                else if (c == '.' && end + 1 < text.Length && char.IsAsciiLetterOrDigit(text[end + 1]))
                {
                    periods++;
                    end++;
                }
                else if (c is '-' or '_')
                {
                    end++;
                }
                else if (c == '@')
                {
                    // Not a valid domain; try the next @ on the following pass.
                    valid = false;
                    break;
                }
                else
                {
                    break;
                }
            }

            if (!valid || periods == 0 || end - at < 2)
            {
                search = at + 1;
                continue;
            }

            char last = text[end - 1];
            if (!char.IsAsciiLetterOrDigit(last))
            {
                // Addresses may not end in '-' or '_'. A trailing '.' was already
                // excluded by the scanner above.
                search = end;
                continue;
            }

            if (start > cursor)
            {
                replacement.Add(new MarkdownNode(NodeType.Text) { Literal = text[cursor..start] });
            }

            string address = text[start..end];
            var link = new MarkdownNode(NodeType.Link)
            {
                Destination = "mailto:" + address,
                Title = string.Empty
            };
            link.AppendChild(new MarkdownNode(NodeType.Text) { Literal = address });
            replacement.Add(link);

            cursor = end;
            search = end;
        }

        if (replacement.Count == 0)
        {
            return;
        }

        if (cursor < text.Length)
        {
            replacement.Add(new MarkdownNode(NodeType.Text) { Literal = text[cursor..] });
        }

        MarkdownNode anchor = textNode;
        foreach (MarkdownNode segment in replacement)
        {
            anchor.InsertAfter(segment);
            anchor = segment;
        }

        textNode.Unlink();
    }

    private static bool IsLocalCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+';
}
