using System;
using System.Collections.Generic;

namespace MDViewer.Markdown;

/// <summary>
/// A node in the Markdown syntax tree. A single class is used for all node kinds,
/// with the unused properties left at their defaults. This mirrors the compact
/// design of the CommonMark reference parser and keeps tree manipulation simple.
/// </summary>
public sealed class MarkdownNode
{
    private MarkdownNode? _firstChild;
    private MarkdownNode? _lastChild;
    private MarkdownNode? _previous;
    private MarkdownNode? _next;

    public MarkdownNode(NodeType type)
    {
        Type = type;
    }

    public NodeType Type { get; }

    public MarkdownNode? Parent { get; private set; }
    public MarkdownNode? FirstChild => _firstChild;
    public MarkdownNode? LastChild => _lastChild;
    public MarkdownNode? Previous => _previous;
    public MarkdownNode? Next => _next;

    public IEnumerable<MarkdownNode> Children
    {
        get
        {
            for (MarkdownNode? child = _firstChild; child is not null; child = child._next)
            {
                yield return child;
            }
        }
    }

    /// <summary>Text content for text, code, code block, and raw HTML nodes.</summary>
    public string Literal { get; set; } = string.Empty;

    /// <summary>URL of a link or image.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Optional title of a link or image.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Fence info string (the language name is its first word).</summary>
    public string Info { get; set; } = string.Empty;

    /// <summary>ATX/setext heading level, 1-6.</summary>
    public int HeadingLevel { get; set; }

    public ListData? ListData { get; set; }
    public bool IsTaskListItem { get; set; }
    public bool TaskChecked { get; set; }

    public TableAlignment[]? TableAlignments { get; set; }
    public TableAlignment CellAlignment { get; set; }
    public bool IsTableHeader { get; set; }

    // Block-parser internal state. It is intentionally not part of the public
    // document model: application code should only read the finished tree.

    internal string StringContent { get; set; } = string.Empty;
    internal bool IsOpen { get; set; } = true;
    internal int SourceStartLine { get; set; } = 1;
    internal int SourceEndLine { get; set; } = 1;
    internal bool IsFenced { get; set; }
    internal char FenceChar { get; set; }
    internal int FenceLength { get; set; }
    internal int FenceOffset { get; set; }
    internal int HtmlBlockType { get; set; }

    public void AppendChild(MarkdownNode child)
    {
        child.Unlink();
        child.Parent = this;
        child._previous = _lastChild;
        if (_lastChild is null)
        {
            _firstChild = child;
        }
        else
        {
            _lastChild._next = child;
        }

        _lastChild = child;
    }

    public void InsertAfter(MarkdownNode sibling)
    {
        if (Parent is null)
        {
            throw new InvalidOperationException("Cannot insert a sibling on a detached node.");
        }

        sibling.Unlink();
        sibling.Parent = Parent;
        sibling._previous = this;
        sibling._next = _next;
        if (_next is null)
        {
            Parent._lastChild = sibling;
        }
        else
        {
            _next._previous = sibling;
        }

        _next = sibling;
    }

    public void Unlink()
    {
        if (Parent is null)
        {
            return;
        }

        if (_previous is null)
        {
            Parent._firstChild = _next;
        }
        else
        {
            _previous._next = _next;
        }

        if (_next is null)
        {
            Parent._lastChild = _previous;
        }
        else
        {
            _next._previous = _previous;
        }

        Parent = null;
        _previous = null;
        _next = null;
    }

    public override string ToString() => Type.ToString();
}
