using System.Text.RegularExpressions;

namespace Md2Html.Markdown;

/// <summary>
/// GFM task list items: a list item whose first block is a paragraph beginning with
/// <c>[ ]</c> or <c>[x]</c>. The marker is removed here (before inline parsing) and
/// the renderer adds the checkbox element in its place.
/// </summary>
internal static class TaskListProcessor
{
    private static readonly Regex Marker = new(@"^[ \t]*\[([ xX])\][ \t]+", RegexOptions.Compiled);

    public static void Transform(MarkdownNode document)
    {
        TransformChildren(document);
    }

    private static void TransformChildren(MarkdownNode node)
    {
        foreach (MarkdownNode child in node.Children)
        {
            if (child.Type == NodeType.Item &&
                child.FirstChild is { Type: NodeType.Paragraph } paragraph)
            {
                Match match = Marker.Match(paragraph.StringContent);
                if (match.Success)
                {
                    child.IsTaskListItem = true;
                    child.TaskChecked = match.Groups[1].Value is "x" or "X";
                    paragraph.StringContent = paragraph.StringContent[match.Length..];
                }
            }

            TransformChildren(child);
        }
    }
}
