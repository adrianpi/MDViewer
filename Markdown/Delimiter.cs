namespace Md2Html.Markdown;

/// <summary>An entry in the inline parser's delimiter stack for * _ and ~~.</summary>
internal sealed class Delimiter
{
    public required char Character { get; init; }
    public required MarkdownNode Node { get; init; }
    public required bool CanOpen { get; init; }
    public required bool CanClose { get; init; }
    public required int OriginalLength { get; init; }
    public int Length { get; set; }
    public Delimiter? Previous { get; set; }
    public Delimiter? Next { get; set; }
}

/// <summary>An entry in the inline parser's bracket stack for links and images.</summary>
internal sealed class Bracket
{
    public required MarkdownNode Node { get; init; }
    public required int Index { get; init; }
    public required bool IsImage { get; init; }
    public Delimiter? PreviousDelimiter { get; init; }
    public Bracket? Previous { get; init; }
    public bool IsActive { get; set; } = true;
    public bool HasBracketAfter { get; set; }
}
