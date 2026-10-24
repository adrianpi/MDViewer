namespace Md2Html.Markdown;

public enum ListType
{
    Bullet,
    Ordered
}

/// <summary>
/// The properties that CommonMark associates with a list and with each item in it.
/// A single instance is shared by the list and its items, mirroring the reference
/// implementation: an item's marker determines how its continuation lines are indented.
/// </summary>
public sealed class ListData
{
    public ListType Type { get; init; }
    public char? BulletChar { get; init; }
    public int? Start { get; init; }
    public char? Delimiter { get; init; }
    public int MarkerOffset { get; init; }
    public int Padding { get; set; }

    /// <summary>Lists are tight unless a blank line separates items or item content.</summary>
    public bool IsTight { get; set; } = true;
}

public enum TableAlignment
{
    None,
    Left,
    Center,
    Right
}

/// <summary>A parsed link reference definition from <c>[label]: destination "title"</c>.</summary>
public readonly record struct LinkReference(string Destination, string Title);
