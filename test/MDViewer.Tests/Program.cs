using System.Text.Json;
using MDViewer.Markdown;

namespace MDViewer.Tests;

/// <summary>
/// Small dependency-free test runner for the Markdown engine. It executes the
/// official CommonMark and GFM example suites and a few focused option checks.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static int Main()
    {
        var failures = new List<string>();
        int failed = 0;

        var commonMarkOptions = new MarkdownOptions
        {
            Tables = false,
            TaskLists = false,
            Strikethrough = false,
            AutolinkLiterals = false
        };

        failed += RunSpec(
            "CommonMark",
            "commonmark-spec.json",
            new MarkdownConverter(commonMarkOptions),
            failures);

        failed += RunSpec(
            "GFM extensions",
            "gfm-spec.json",
            new MarkdownConverter(new MarkdownOptions()),
            failures);

        failed += RunFocusedTests(failures);

        if (failed > 0)
        {
            Console.WriteLine();
            foreach (string failure in failures)
            {
                Console.WriteLine(failure);
                Console.WriteLine();
            }

            Console.WriteLine($"{failed} test(s) failed.");
            return 1;
        }

        Console.WriteLine("All tests passed.");
        return 0;
    }

    private static int RunSpec(
        string name,
        string fileName,
        MarkdownConverter converter,
        List<string> failures)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        List<SpecCase> cases = JsonSerializer.Deserialize<List<SpecCase>>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Could not read {fileName}.");

        int failed = 0;
        int ran = 0;
        int skipped = 0;

        foreach (SpecCase example in cases)
        {
            if (example.Disabled)
            {
                skipped++;
                continue;
            }

            ran++;
            string actual;
            try
            {
                actual = converter.ToHtml(example.Markdown);
            }
            catch (Exception ex)
            {
                actual = "EXCEPTION: " + ex;
            }

            if (actual == example.Html)
            {
                continue;
            }

            failed++;
            if (failed <= 10)
            {
                failures.Add(
                    $"{name} example {example.Example} [{example.Section}]\n" +
                    $"  markdown: {Visible(example.Markdown)}\n" +
                    $"  expected: {Visible(example.Html)}\n" +
                    $"  actual:   {Visible(actual)}");
            }
        }

        string skippedText = skipped > 0 ? $", {skipped} disabled" : string.Empty;
        Console.WriteLine($"{name}: {ran - failed}/{ran} passed{skippedText}");
        return failed;
    }

    private static int RunFocusedTests(List<string> failures)
    {
        var converter = new MarkdownConverter();
        int failed = 0;

        failed += Check(
            "Task list items",
            "<ul>\n" +
            "<li><input type=\"checkbox\" disabled=\"\" /> foo</li>\n" +
            "<li><input type=\"checkbox\" checked=\"\" disabled=\"\" /> bar</li>\n" +
            "</ul>\n",
            converter.ToHtml("- [ ] foo\n- [x] bar\n"),
            failures);

        var safeConverter = new MarkdownConverter(new MarkdownOptions { AllowRawHtml = false });
        failed += Check(
            "Raw HTML can be disabled",
            "<p>text &lt;b&gt;bold&lt;/b&gt;</p>\n",
            safeConverter.ToHtml("text <b>bold</b>"),
            failures);

        Console.WriteLine($"Focused: {2 - failed}/2 passed");
        return failed;
    }

    private static int Check(
        string name,
        string expected,
        string actual,
        List<string> failures)
    {
        if (actual == expected)
        {
            return 0;
        }

        failures.Add(
            $"{name}\n" +
            $"  expected: {Visible(expected)}\n" +
            $"  actual:   {Visible(actual)}");
        return 1;
    }

    private static string Visible(string value) =>
        value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private sealed class SpecCase
    {
        public string Markdown { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
        public int Example { get; set; }
        public string Section { get; set; } = string.Empty;
        public bool Disabled { get; set; }
    }
}
