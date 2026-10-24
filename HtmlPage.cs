using System;
using System.IO;
using System.Text;

namespace Md2Html;

/// <summary>
/// Wraps the HTML fragment produced by the Markdown engine in a complete document.
/// Keeping this in the UI layer means the converter stays a pure Markdown-to-HTML
/// transformation and can be reused without any WinForms dependency.
/// </summary>
internal static class HtmlPage
{
    private const string LinkScript = """
        function md2htmlOnClick(event) {
            event = event || window.event;
            if (event.defaultPrevented || event.returnValue === false) {
                return;
            }
            var element = event.target || event.srcElement;
            while (element && element.tagName &&
                   element.tagName.toLowerCase() !== 'a' &&
                   element.tagName.toLowerCase() !== 'area') {
                element = element.parentElement || element.parentNode;
            }
            if (!element || !element.getAttribute) {
                return;
            }
            var href = element.getAttribute('href');
            if (href === null || href === '') {
                return;
            }
            // javascript: links are part of raw HTML; let the browser handle them.
            if (/^\s*javascript:/i.test(href)) {
                return;
            }
            try {
                window.external.OpenLink(href, element.href || href);
            } catch (error) {
                // Object scripting can be unavailable in unusual environments.
                // In that case keep the normal browser behavior.
                return;
            }
            if (event.preventDefault) {
                event.preventDefault();
            }
            event.returnValue = false;
            if (event.stopPropagation) {
                event.stopPropagation();
            }
            return false;
        }
        if (document.addEventListener) {
            document.addEventListener('click', md2htmlOnClick, false);
        } else if (document.attachEvent) {
            document.attachEvent('onclick', md2htmlOnClick);
        }
        """;

    private const string FindScript = """
        function md2htmlFindText(text) {
            if (!text || !document.body || !document.body.createTextRange) {
                return false;
            }

            var range = null;
            try {
                if (document.selection) {
                    range = document.selection.createRange();
                }
            } catch (error) {
                range = null;
            }

            // A control selection or a selection outside the page cannot be used
            // for text search; in that case begin at the start of the document.
            var parent = null;
            try {
                if (range && typeof range.parentElement === 'function') {
                    parent = range.parentElement();
                }
            } catch (error) {
                parent = null;
            }
            if (!range || typeof range.findText !== 'function' ||
                !parent || (parent !== document.body && !document.body.contains(parent))) {
                range = document.body.createTextRange();
            }

            try {
                range.collapse(false);
            } catch (error) {
                range = document.body.createTextRange();
            }

            if (!range.findText(text)) {
                return false;
            }

            range.select();
            range.scrollIntoView(true);
            return true;
        }
        """;

    private const string Style = """
        html { -ms-text-size-adjust: 100%; }
        ::selection { background: #ffdf70; color: #1f2328; }
        body {
            font-family: "Segoe UI", "Helvetica Neue", Arial, sans-serif;
            font-size: 15px;
            line-height: 1.6;
            color: #1f2328;
            background: #ffffff;
            max-width: 980px;
            margin: 0 auto;
            padding: 28px 32px 80px;
            word-wrap: break-word;
        }
        h1, h2, h3, h4, h5, h6 {
            margin-top: 24px;
            margin-bottom: 16px;
            font-weight: 600;
            line-height: 1.25;
        }
        h1 { font-size: 2em; border-bottom: 1px solid #d8dee4; padding-bottom: .3em; }
        h2 { font-size: 1.5em; border-bottom: 1px solid #d8dee4; padding-bottom: .3em; }
        h3 { font-size: 1.25em; }
        h4 { font-size: 1em; }
        h5 { font-size: .875em; }
        h6 { font-size: .85em; color: #59636e; }
        p, blockquote, ul, ol, dl, table, pre { margin-top: 0; margin-bottom: 16px; }
        a { color: #0969da; text-decoration: none; }
        a:hover { text-decoration: underline; }
        strong { font-weight: 600; }
        hr {
            height: .25em;
            padding: 0;
            margin: 24px 0;
            background-color: #d0d7de;
            border: 0;
        }
        blockquote {
            margin: 0 0 16px;
            padding: 0 1em;
            color: #59636e;
            border-left: .25em solid #d0d7de;
        }
        ul, ol { padding-left: 2em; }
        li + li { margin-top: .25em; }
        li > input[type="checkbox"] {
            /* GFM task list checkboxes are disabled inputs, not ::marker content. */
            margin: 0 .4em .2em -1.35em;
            vertical-align: middle;
        }
        code, kbd, samp {
            font-family: Consolas, "Cascadia Mono", "Courier New", monospace;
            font-size: 85%;
        }
        code {
            padding: .2em .4em;
            background-color: #eff1f3;
            border-radius: 3px;
        }
        pre {
            padding: 16px;
            overflow: auto;
            background-color: #f6f8fa;
            border-radius: 6px;
            line-height: 1.45;
        }
        pre code {
            padding: 0;
            background: transparent;
            font-size: 90%;
            white-space: pre;
        }
        table {
            border-collapse: collapse;
            border-spacing: 0;
            display: block;
            width: 100%;
            overflow: auto;
        }
        table th { font-weight: 600; }
        table th, table td {
            padding: 6px 13px;
            border: 1px solid #d0d7de;
        }
        table tr:nth-child(2n) { background-color: #f6f8fa; }
        img { max-width: 100%; box-sizing: content-box; }
        .welcome {
            margin: 12vh auto 0;
            max-width: 520px;
            text-align: center;
            color: #59636e;
        }
        .welcome h1 { border: 0; color: #1f2328; }
        .welcome kbd {
            padding: .1em .35em;
            border: 1px solid #d0d7de;
            border-bottom-width: 2px;
            border-radius: 4px;
            background: #f6f8fa;
        }
        """;

    public static string Build(string bodyHtml, string? baseDirectory)
    {
        string baseTag = string.Empty;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            // A <base> element makes relative image and link URLs resolve against the
            // folder containing the Markdown file rather than against about:blank.
            string directoryUri = new Uri(
                baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar).AbsoluteUri;
            baseTag = $"<base href=\"{EscapeAttribute(directoryUri)}\">";
        }

        var html = new StringBuilder(bodyHtml.Length + Style.Length + LinkScript.Length + 512);
        html.Append("<!DOCTYPE html>\n<html>\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n");
        html.Append(baseTag).Append('\n');
        html.Append("<style>\n").Append(Style).Append("\n</style>\n");
        // The WebBrowser treats DocumentText as about:blank. Clicks on file:///
        // links are swallowed in that origin, so all link clicks are routed
        // through the hosting object rather than relying on Navigating events.
        html.Append("<script type=\"text/javascript\">\n").Append(LinkScript).Append("\n</script>\n");
        html.Append("<script type=\"text/javascript\">\n").Append(FindScript).Append("\n</script>\n");
        html.Append("</head>\n<body>\n");
        html.Append(bodyHtml);
        html.Append("\n</body>\n</html>\n");
        return html.ToString();
    }

    public static string BuildWelcome()
    {
        const string body = """
            <div class="welcome">
              <h1>Markdown Viewer</h1>
              <p>Open a Markdown file to render it as HTML.</p>
              <p>Use <kbd>Ctrl</kbd>+<kbd>O</kbd> or drop a <code>.md</code> file onto this window.</p>
            </div>
            """;
        return Build(body, null);
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;");
}
