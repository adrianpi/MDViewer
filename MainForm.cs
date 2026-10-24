using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Md2Html;

/// <summary>
/// Viewer window. All Markdown knowledge lives in <see cref="Markdown.MarkdownConverter"/>;
/// this class only handles files, menus, drag and drop, and the embedded browser.
/// </summary>
public sealed class MainForm : Form, IMessageFilter
{
    private const int WmKeyDown = 0x0100;

    private static readonly string[] MarkdownExtensions =
        [".md", ".markdown", ".mdown", ".mkd", ".mdwn", ".mkdn"];

    private readonly Markdown.MarkdownConverter _converter = new();
    private readonly WebBrowser _browser;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripLabel _fileLabel;
    private readonly ToolStripMenuItem _reloadMenuItem;
    private readonly ToolStripButton _reloadButton;
    private readonly ToolStripMenuItem _printMenuItem;

    private string? _currentPath;
    private string _lastSearchText = string.Empty;
    private bool _printPreviewOpen;

    public MainForm(string? initialFile = null)
    {
        Text = "Markdown Viewer";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);
        Size = new Size(1100, 800);
        KeyPreview = true;

        // ---- menu ---------------------------------------------------------
        var fileMenu = new ToolStripMenuItem("&File");
        var openMenuItem = new ToolStripMenuItem("&Open...", null, OpenClicked)
        {
            ShortcutKeys = Keys.Control | Keys.O
        };
        _reloadMenuItem = new ToolStripMenuItem("&Reload", null, ReloadClicked)
        {
            ShortcutKeys = Keys.F5,
            Enabled = false
        };
        _printMenuItem = new ToolStripMenuItem("&Print...", null, PrintClicked)
        {
            ShortcutKeys = Keys.Control | Keys.P,
            Enabled = false
        };
        fileMenu.DropDownItems.Add(openMenuItem);
        fileMenu.DropDownItems.Add(_reloadMenuItem);
        fileMenu.DropDownItems.Add(_printMenuItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var editMenu = new ToolStripMenuItem("&Edit");
        var findMenuItem = new ToolStripMenuItem("&Find...", null, FindClicked)
        {
            ShortcutKeys = Keys.Control | Keys.F
        };
        editMenu.DropDownItems.Add(findMenuItem);

        var menu = new MenuStrip();
        menu.Items.Add(fileMenu);
        menu.Items.Add(editMenu);
        menu.Dock = DockStyle.Fill;

        // ---- toolbar ------------------------------------------------------
        var openButton = new ToolStripButton("Open")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Open a Markdown file (Ctrl+O)"
        };
        openButton.Click += OpenClicked;

        _reloadButton = new ToolStripButton("Reload")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Re-render the current file (F5)",
            Enabled = false
        };
        _reloadButton.Click += ReloadClicked;

        _fileLabel = new ToolStripLabel("No file open")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = SystemColors.GrayText
        };

        var toolbar = new ToolStrip();
        toolbar.GripStyle = ToolStripGripStyle.Hidden;
        toolbar.Items.Add(openButton);
        toolbar.Items.Add(_reloadButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_fileLabel);

        // ---- web view -----------------------------------------------------
        _browser = new WebBrowser
        {
            Dock = DockStyle.Fill,
            AllowWebBrowserDrop = false,
            WebBrowserShortcutsEnabled = true,
            IsWebBrowserContextMenuEnabled = true,
            ScriptErrorsSuppressed = true
        };
        _browser.Navigating += BrowserNavigating;
        // Let the generated page ask the form to handle a link click. This is
        // necessary because WebBrowser does not raise Navigating for file:///
        // links when the page itself was loaded with DocumentText/about:blank.
        _browser.ObjectForScripting = new BrowserLinkHost(this);
        _browser.DocumentText = HtmlPage.BuildWelcome();

        // ---- status bar ---------------------------------------------------
        var status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Open a Markdown file to begin.")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        status.Items.Add(_statusLabel);
        status.Dock = DockStyle.Fill;

        // A TableLayoutPanel gives a deterministic layout independent of z-order.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(menu, 0, 0);
        layout.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(_browser, 0, 2);
        layout.Controls.Add(status, 0, 3);
        Controls.Add(layout);

        AllowDrop = true;
        DragEnter += FormDragEnter;
        DragDrop += FormDragDrop;
        Application.AddMessageFilter(this);
        FormClosed += (_, _) => Application.RemoveMessageFilter(this);
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialFile))
            {
                LoadDocument(initialFile);
            }
        };
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmKeyDown ||
            (ModifierKeys & Keys.Control) == 0 ||
            (ModifierKeys & (Keys.Alt | Keys.Shift)) != 0)
        {
            return false;
        }

        // The WebBrowser ActiveX control handles Ctrl+P and Ctrl+F itself before
        // the menu shortcut sees them, so intercept both at the message loop.
        if (m.WParam == (IntPtr)Keys.P)
        {
            PrintClicked(this, EventArgs.Empty);
            return true;
        }

        if (m.WParam == (IntPtr)Keys.F && ActiveForm == this)
        {
            FindClicked(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Displays the modal find dialog and keeps its search text for the next use.
    /// </summary>
    private void FindClicked(object? sender, EventArgs e)
    {
        using var dialog = new Form
        {
            Text = "Find Text",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 104)
        };

        var searchLabel = new Label
        {
            Text = "Search:",
            AutoSize = true,
            Location = new Point(12, 14)
        };
        var searchBox = new TextBox
        {
            Location = new Point(70, 11),
            Width = 278,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = _lastSearchText
        };
        var findButton = new Button
        {
            Text = "Find",
            DialogResult = DialogResult.None,
            Location = new Point(268, 60),
            Size = new Size(80, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        findButton.Click += (_, _) =>
        {
            _lastSearchText = searchBox.Text;
            FindText(searchBox.Text);
        };
        dialog.FormClosed += (_, _) => _lastSearchText = searchBox.Text;
        dialog.Controls.Add(searchLabel);
        dialog.Controls.Add(searchBox);
        dialog.Controls.Add(findButton);
        dialog.AcceptButton = findButton;
        dialog.Shown += (_, _) =>
        {
            searchBox.Focus();
            searchBox.SelectAll();
        };

        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Finds the next occurrence in the rendered page, starting at the current
    /// browser selection or caret position.
    /// </summary>
    private void FindText(string text)
    {
        if (text.Length == 0 || _browser.Document is null)
        {
            return;
        }

        object? result = _browser.Document.InvokeScript("md2htmlFindText", [text]);
        if (!Equals(result, true))
        {
            MessageBox.Show(this, "Not found", "Find Text",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Markdown file",
            Filter = "Markdown files (*.md;*.markdown;*.mdown)|*.md;*.markdown;*.mdown;*.mkd;*.mdwn;*.mkdn|" +
                     "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadDocument(dialog.FileName);
        }
    }

    private void ReloadClicked(object? sender, EventArgs e)
    {
        if (_currentPath is not null)
        {
            LoadDocument(_currentPath);
        }
    }

    private void PrintClicked(object? sender, EventArgs e)
    {
        if (_currentPath is null || _printPreviewOpen)
        {
            return;
        }

        _printPreviewOpen = true;
        try
        {
            using var document = new HtmlPrintDocument(_browser);
            using var preview = new PrintPreviewDialog
            {
                Document = document,
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(1000, 800)
            };
            preview.ShowDialog(this);
        }
        finally
        {
            _printPreviewOpen = false;
        }
    }

    private void LoadDocument(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string markdown = ReadAllText(fullPath);

            long start = Stopwatch.GetTimestamp();
            string body = _converter.ToHtml(markdown);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            // The body is inserted as a fragment into a complete document with a
            // GitHub-like stylesheet and a <base> for relative resources.
            _browser.DocumentText = HtmlPage.Build(body, Path.GetDirectoryName(fullPath));

            _currentPath = fullPath;
            _reloadMenuItem.Enabled = true;
            _reloadButton.Enabled = true;
            _printMenuItem.Enabled = true;
            _fileLabel.Text = Path.GetFileName(fullPath);
            _fileLabel.ToolTipText = fullPath;
            Text = $"{Path.GetFileName(fullPath)} - Markdown Viewer";
            _statusLabel.Text = $"Rendered {markdown.Length:N0} characters in {elapsed.TotalMilliseconds:N0} ms.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _statusLabel.Text = "Could not open the file.";
            MessageBox.Show(this, ex.Message, "Unable to open file",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ReadAllText(string path)
    {
        // StreamReader honours UTF-8/UTF-16 BOMs and otherwise assumes UTF-8,
        // which is the de facto encoding of Markdown files.
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void BrowserNavigating(object? sender, WebBrowserNavigatingEventArgs e)
    {
        Uri? target = e.Url;
        if (target is null || target.Scheme == "about")
        {
            // Initial DocumentText load or an in-page fragment.
            return;
        }

        // Link clicks normally arrive through HandleLink below. This is a fallback
        // for navigations started by raw HTML scripts or by the browser itself.
        e.Cancel = true;
        HandleUri(target);
    }

    /// <summary>
    /// Handles a link chosen in the document. The page's click handler supplies both
    /// the original href and the browser-resolved absolute URI.
    /// </summary>
    internal void HandleLink(string? rawHref, string? absoluteHref)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string?, string?>(HandleLink), rawHref, absoluteHref);
            return;
        }

        if (string.IsNullOrWhiteSpace(rawHref))
        {
            return;
        }

        string raw = rawHref.Trim();
        if (raw.StartsWith('#'))
        {
            ScrollToFragment(raw);
            return;
        }

        if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Handle the file:/// prefix literally. For a well-formed absolute file URL
        // the remainder starts with a drive letter (or a UNC path); for the less
        // common file:///relative.md form it is resolved against the current file.
        const string filePrefix = "file:///";
        if (raw.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string encodedPath = raw[filePrefix.Length..];
            int suffix = encodedPath.IndexOfAny(['?', '#']);
            if (suffix >= 0)
            {
                encodedPath = encodedPath[..suffix];
            }

            string path = Uri.UnescapeDataString(encodedPath);
            path = path.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(path) && _currentPath is not null)
            {
                path = Path.Combine(
                    Path.GetDirectoryName(_currentPath) ?? Environment.CurrentDirectory,
                    path);
            }

            HandleLocalPath(path);
            return;
        }

        if (Uri.TryCreate(absoluteHref, UriKind.Absolute, out Uri? target))
        {
            HandleUri(target);
            return;
        }

        // Last resort for a browser that supplies only a raw path.
        HandleRawPath(raw);
    }

    private void HandleUri(Uri target)
    {
        if (target.IsFile)
        {
            // Uri.LocalPath removes the file:/// prefix and percent-decodes the path.
            HandleLocalPath(target.LocalPath);
            return;
        }

        if (target.Scheme is "javascript" or "about")
        {
            return;
        }

        // This includes http, https, ftp, mailto, and registered custom schemes.
        OpenExternally(target.AbsoluteUri);
    }

    private void HandleLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (IsMarkdownPath(path))
        {
            // Starting the load through the message queue lets the browser finish
            // processing the click before its document is replaced.
            string markdownFile = path;
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() => LoadDocument(markdownFile)));
            }

            return;
        }

        // Any non-Markdown local target (an image, PDF, folder, executable, ...)
        // is handed to the Windows shell, just like double-clicking the file.
        OpenExternally(path);
    }

    private void HandleRawPath(string raw)
    {
        string path = raw;
        int suffix = path.IndexOfAny(['?', '#']);
        if (suffix >= 0)
        {
            path = path[..suffix];
        }

        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(path) && _currentPath is not null)
        {
            path = Path.Combine(
                Path.GetDirectoryName(_currentPath) ?? Environment.CurrentDirectory,
                path);
        }

        HandleLocalPath(path);
    }

    private void ScrollToFragment(string rawHref)
    {
        HtmlDocument? document = _browser.Document;
        if (document is null)
        {
            return;
        }

        string fragment = rawHref[1..];
        string id;
        try
        {
            id = Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            id = fragment;
        }

        HtmlElement? target = id.Length == 0 ? document.Body : document.GetElementById(id);
        target ??= FindAnchorByName(document, id);
        target?.ScrollIntoView(true);
    }

    private static HtmlElement? FindAnchorByName(HtmlDocument document, string name)
    {
        foreach (HtmlElement anchor in document.GetElementsByTagName("a"))
        {
            if (string.Equals(anchor.GetAttribute("name"), name, StringComparison.Ordinal))
            {
                return anchor;
            }
        }

        return null;
    }

    private static bool IsMarkdownPath(string path) =>
        MarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void OpenExternally(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                       System.ComponentModel.Win32Exception or
                                       ArgumentException)
        {
            MessageBox.Show($"Could not open the link:\n{target}\n\n{ex.Message}",
                "Open link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void FormDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDroppedMarkdownFile(e.Data) is null
            ? DragDropEffects.None
            : DragDropEffects.Copy;
    }

    private void FormDragDrop(object? sender, DragEventArgs e)
    {
        string? file = TryGetDroppedMarkdownFile(e.Data);
        if (file is not null)
        {
            LoadDocument(file);
        }
    }

    private static string? TryGetDroppedMarkdownFile(IDataObject? data)
    {
        if (data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
        {
            return null;
        }

        string file = files[0];
        return MarkdownExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
            ? file
            : null;
    }
}
