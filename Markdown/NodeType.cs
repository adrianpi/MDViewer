namespace Md2Html.Markdown;

/// <summary>
/// The node kinds of the Markdown syntax tree. The first group represents blocks,
/// the second group represents inlines.
/// </summary>
public enum NodeType
{
    Document,
    BlockQuote,
    List,
    Item,
    Paragraph,
    Heading,
    ThematicBreak,
    CodeBlock,
    HtmlBlock,

    // GitHub extension blocks
    Table,
    TableRow,
    TableCell,

    Text,
    SoftBreak,
    LineBreak,
    Code,
    HtmlInline,
    Emphasis,
    Strong,
    Strikethrough,
    Link,
    Image
}
