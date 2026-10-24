using System;
using System.Text;

namespace MDViewer.Markdown;

/// <summary>
/// Converts the syntax tree to HTML. The formatting (line breaks and tag layout)
/// intentionally follows the CommonMark reference renderer so that output is stable
/// and matches the specification examples.
/// </summary>
internal sealed class HtmlRenderer
{
    private readonly MarkdownOptions _options;
    private readonly StringBuilder _buffer = new();
    private string _lastOutput = "\n";
    private int _disableTags;
    private bool _inTableHeader;
    private bool _tableBodyOpen;

    public HtmlRenderer(MarkdownOptions options)
    {
        _options = options;
    }

    public string Render(MarkdownNode document)
    {
        _buffer.Clear();
        _lastOutput = "\n";
        RenderChildren(document);
        return _buffer.ToString();
    }

    private void RenderNode(MarkdownNode node)
    {
        switch (node.Type)
        {
            case NodeType.Document:
                RenderChildren(node);
                break;

            case NodeType.BlockQuote:
                CarriageReturn();
                Tag("blockquote");
                CarriageReturn();
                RenderChildren(node);
                CarriageReturn();
                Tag("/blockquote");
                CarriageReturn();
                break;

            case NodeType.List:
            {
                string tag = node.ListData!.Type == ListType.Bullet ? "ul" : "ol";
                CarriageReturn();
                if (node.ListData.Type == ListType.Ordered &&
                    node.ListData.Start is int start && start != 1)
                {
                    Append("<").Append(tag).Append(" start=\"").Append(start).Append("\">");
                    MarkLastOutput();
                }
                else
                {
                    Tag(tag);
                }

                CarriageReturn();
                RenderChildren(node);
                CarriageReturn();
                Tag("/" + tag);
                CarriageReturn();
                break;
            }

            case NodeType.Item:
                Tag("li");
                if (node.IsTaskListItem)
                {
                    Append("<input type=\"checkbox\"");
                    if (node.TaskChecked)
                    {
                        Append(" checked=\"\"");
                    }

                    Append(" disabled=\"\" /> ");
                }

                RenderChildren(node);
                Tag("/li");
                CarriageReturn();
                break;

            case NodeType.Paragraph:
            {
                bool tight = node.Parent is { Type: NodeType.Item, Parent: { Type: NodeType.List } list } &&
                             list.ListData!.IsTight;
                if (!tight)
                {
                    CarriageReturn();
                    Tag("p");
                }

                RenderChildren(node);

                if (!tight)
                {
                    Tag("/p");
                    CarriageReturn();
                }

                break;
            }

            case NodeType.Heading:
                CarriageReturn();
                Tag("h" + node.HeadingLevel);
                RenderChildren(node);
                Tag("/h" + node.HeadingLevel);
                CarriageReturn();
                break;

            case NodeType.ThematicBreak:
                CarriageReturn();
                TagSelfClosing("hr");
                CarriageReturn();
                break;

            case NodeType.CodeBlock:
            {
                string firstWord = node.Info.Length == 0
                    ? string.Empty
                    : node.Info.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

                CarriageReturn();
                Tag("pre");
                if (firstWord.Length > 0)
                {
                    string className = firstWord.StartsWith("language-", StringComparison.Ordinal)
                        ? firstWord
                        : "language-" + firstWord;
                    Append("<code class=\"").Append(StringUtil.EscapeHtml(className)).Append("\">");
                    MarkLastOutput();
                }
                else
                {
                    Tag("code");
                }

                Out(node.Literal);
                Tag("/code");
                Tag("/pre");
                CarriageReturn();
                break;
            }

            case NodeType.HtmlBlock:
                CarriageReturn();
                if (_options.AllowRawHtml)
                {
                    Literal(node.Literal);
                }
                else
                {
                    Out(node.Literal);
                }

                CarriageReturn();
                break;

            case NodeType.Table:
                CarriageReturn();
                Tag("table");
                _inTableHeader = false;
                _tableBodyOpen = false;
                foreach (MarkdownNode child in node.Children)
                {
                    RenderNode(child);
                }

                if (_tableBodyOpen)
                {
                    CarriageReturn();
                    Tag("/tbody");
                }

                CarriageReturn();
                Tag("/table");
                CarriageReturn();
                break;

            case NodeType.TableRow:
                if (node.IsTableHeader)
                {
                    _inTableHeader = true;
                    CarriageReturn();
                    Tag("thead");
                    CarriageReturn();
                }
                else if (!_tableBodyOpen)
                {
                    CarriageReturn();
                    Tag("tbody");
                    CarriageReturn();
                    _tableBodyOpen = true;
                }

                CarriageReturn();
                Tag("tr");
                foreach (MarkdownNode cell in node.Children)
                {
                    RenderNode(cell);
                }

                CarriageReturn();
                Tag("/tr");
                if (node.IsTableHeader)
                {
                    CarriageReturn();
                    Tag("/thead");
                    _inTableHeader = false;
                }

                break;

            case NodeType.TableCell:
                CarriageReturn();
                string cellTag = _inTableHeader ? "th" : "td";
                Append("<").Append(cellTag);
                switch (node.CellAlignment)
                {
                    case TableAlignment.Left:
                        Append(" align=\"left\"");
                        break;
                    case TableAlignment.Center:
                        Append(" align=\"center\"");
                        break;
                    case TableAlignment.Right:
                        Append(" align=\"right\"");
                        break;
                }

                Append(">");
                MarkLastOutput();
                RenderChildren(node);
                Tag("/" + cellTag);
                break;

            case NodeType.Text:
                Out(node.Literal);
                break;

            case NodeType.SoftBreak:
                Literal("\n");
                break;

            case NodeType.LineBreak:
                TagSelfClosing("br");
                CarriageReturn();
                break;

            case NodeType.Code:
                Tag("code");
                Out(node.Literal);
                Tag("/code");
                break;

            case NodeType.HtmlInline:
                if (_disableTags > 0)
                {
                    break;
                }

                if (_options.AllowRawHtml)
                {
                    Literal(node.Literal);
                }
                else
                {
                    Out(node.Literal);
                }

                break;

            case NodeType.Emphasis:
                Tag("em");
                RenderChildren(node);
                Tag("/em");
                break;

            case NodeType.Strong:
                Tag("strong");
                RenderChildren(node);
                Tag("/strong");
                break;

            case NodeType.Strikethrough:
                Tag("del");
                RenderChildren(node);
                Tag("/del");
                break;

            case NodeType.Link:
                bool showTags = _disableTags == 0;
                if (showTags)
                {
                    Append("<a href=\"").Append(StringUtil.EscapeHtml(node.Destination)).Append('"');
                    if (node.Title.Length > 0)
                    {
                        Append(" title=\"").Append(StringUtil.EscapeHtml(node.Title)).Append('"');
                    }

                    Append(">");
                    MarkLastOutput();
                }

                RenderChildren(node);

                if (showTags)
                {
                    Tag("/a");
                }

                break;

            case NodeType.Image:
                RenderImage(node);
                break;
        }
    }

    private void RenderImage(MarkdownNode node)
    {
        if (_disableTags == 0)
        {
            Append("<img src=\"").Append(StringUtil.EscapeHtml(node.Destination)).Append("\" alt=\"");
            MarkLastOutput();
        }

        _disableTags++;
        RenderChildren(node);
        _disableTags--;

        if (_disableTags == 0)
        {
            if (node.Title.Length > 0)
            {
                Append("\" title=\"").Append(StringUtil.EscapeHtml(node.Title));
            }

            Append("\" />");
            MarkLastOutput();
        }
    }

    private void RenderChildren(MarkdownNode node)
    {
        foreach (MarkdownNode child in node.Children)
        {
            RenderNode(child);
        }
    }

    private void Tag(string name)
    {
        if (_disableTags > 0)
        {
            return;
        }

        if (name.StartsWith('/'))
        {
            Append("</").Append(name.AsSpan(1)).Append('>');
        }
        else
        {
            Append("<").Append(name).Append('>');
        }

        MarkLastOutput();
    }

    private void TagSelfClosing(string name)
    {
        if (_disableTags > 0)
        {
            return;
        }

        Append("<").Append(name).Append(" />");
        MarkLastOutput();
    }

    private void Out(string value)
    {
        Literal(StringUtil.EscapeHtml(value));
    }

    private void Literal(string value)
    {
        Append(value);
        _lastOutput = value;
    }

    private void CarriageReturn()
    {
        if (_lastOutput != "\n")
        {
            Literal("\n");
        }
    }

    private StringBuilder Append(string value) => _buffer.Append(value);

    private void MarkLastOutput() => _lastOutput = ">";
}
