# Markdown Viewer (WinForms)

A self-contained Windows desktop Markdown viewer. It converts CommonMark plus the
GitHub extensions **tables**, **task lists**, **strikethrough**, and **autolink
literals** to HTML and displays the result in the built-in WinForms `WebBrowser`
control.

No NuGet packages, native SDKs, or third-party Markdown libraries are used. The
Markdown parser is implemented in this repository, and the only HTML entity data
comes from the standard data table embedded in the executable.

## Usage

```text
MDViewer.exe "C:\path\to\document.md"
```

The optional command-line path is opened immediately. Inside the window you can
also use **Ctrl+O**, drag a Markdown file onto the window, or press **F5** to
reload the current file after editing it.

Relative images and links resolve against the folder containing the Markdown
file. External links open in the default browser. Links to Markdown files open
inside the viewer; other local files (including `file:///` URLs) are opened with
their registered Windows application.

## Build and run

```powershell
dotnet build -c Release
dotnet run -- "C:\path\to\document.md"
```

The project targets `net10.0-windows`; any Windows .NET 10 SDK is sufficient.

## Windows installer

Install [Inno Setup 6 or 7](https://jrsoftware.org/isinfo.php), then run this from
the repository root:

```powershell
.\installer\Build-Installer.ps1
```

The script publishes a self-contained x64 release and creates
`installer\Output\MDViewer-Setup-1.0.1.exe`. The installer offers an optional,
checked-by-default checkbox for associating `.md` files with MDViewer, plus an
optional desktop shortcut, and creates a Start menu shortcut with standard
Windows uninstall support. The association is per-user and is restored (when
unchanged) if the application is uninstalled. The installer offers to open
Windows Default apps settings so the user can confirm MDViewer for `.md` files
when Windows protects an existing default-app choice.

[Download the MDViewer 1.0.1 installer for Windows x64](installer/Output/MDViewer-Setup-1.0.1.exe).

## Tests

```powershell
dotnet run --project test/MDViewer.Tests
```

The test runner has no external dependencies. It executes the CommonMark 0.31.2
examples and the GitHub Flavored Markdown extension examples, plus a few focused
option checks.

## Structure

- `MarkdownConverter` (`Markdown/MarkdownConverter.cs`) is the public conversion
  API. It has no WinForms dependency and can be reused elsewhere:
  ```csharp
  string html = new MarkdownConverter().ToHtml(markdown);
  ```
- `Markdown/` contains the block parser, inline parser, delimiter/bracket stacks,
  HTML renderer, entity handling, and GitHub extension support.
- `MainForm.cs` contains only the viewer UI: menus, file handling, drag and drop,
  and the embedded browser.
- `HtmlPage.cs` wraps the rendered HTML fragment in a full document with styling.
- `test/MDViewer.Tests/` contains the dependency-free conversion test runner and
  the official example data used by the tests.

## Notes

- CommonMark support is validated against all 652 examples in the CommonMark
  0.31.2 specification. The implemented GitHub extensions also pass the relevant
  examples in the GitHub Flavored Markdown specification.
- Raw HTML is part of CommonMark and is rendered by design. As with an HTML file,
  only open Markdown documents you trust. Clicking a link to a non-Markdown local
  file uses ShellExecute, so links to executables or scripts launch them.
- The WebBrowser control is the browser shipped with Windows. When modern IE11
  rendering is not already enabled for the executable, the application asks
  before adding the per-user `FEATURE_BROWSER_EMULATION` registry value. If you
  decline, it runs in compatibility mode instead; the document still opens and
  links still work, but styling may be simplified.
