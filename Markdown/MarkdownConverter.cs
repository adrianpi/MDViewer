namespace Md2Html.Markdown;

/// <summary>
/// Controls the GitHub-flavoured extensions. The CommonMark core is always enabled;
/// each switch only adds syntax that is not part of the CommonMark specification.
/// </summary>
public sealed class MarkdownOptions
{
    public bool Tables { get; init; } = true;
    public bool TaskLists { get; init; } = true;
    public bool Strikethrough { get; init; } = true;
    public bool AutolinkLiterals { get; init; } = true;

    /// <summary>
    /// CommonMark permits raw HTML inline and in HTML blocks. Disabling it renders
    /// those constructs as text, which is useful when displaying untrusted input.
    /// </summary>
    public bool AllowRawHtml { get; init; } = true;
}

/// <summary>
/// Public entry point for the Markdown engine. This class has no WinForms or browser
/// dependencies; it can be used from a console application or a web service as-is.
/// </summary>
public sealed class MarkdownConverter
{
    private readonly MarkdownOptions _options;

    public MarkdownConverter(MarkdownOptions? options = null)
    {
        _options = options ?? new MarkdownOptions();
    }

    /// <summary>Converts Markdown to an HTML fragment (no &lt;html&gt;/&lt;body&gt; wrapper).</summary>
    public string ToHtml(string markdown)
    {
        MarkdownNode document = Parse(markdown);
        return new HtmlRenderer(_options).Render(document);
    }

    /// <summary>Parses Markdown into a syntax tree, mainly useful for tests and tooling.</summary>
    public MarkdownNode Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return new BlockParser(_options).Parse(markdown);
    }
}
