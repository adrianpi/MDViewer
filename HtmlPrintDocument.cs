using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MDViewer;

/// <summary>
/// Adapts the rendered HTML document to the standard <see cref="PrintPreviewDialog"/>.
/// The WebBrowser control's own IE print preview is not available on current Windows,
/// and DrawToBitmap does not capture the ActiveX surface, so the document body is
/// captured once through MSHTML and paginated for the standard print preview.
/// </summary>
internal sealed class HtmlPrintDocument : PrintDocument
{
    private const string PrintBodyStyle = "font-size: 17.25px;";

    private readonly Bitmap _pageImage;
    private int _sourceTop;

    public HtmlPrintDocument(WebBrowser browser)
    {
        Margins currentMargins = DefaultPageSettings.Margins;
        DefaultPageSettings.Margins = new Margins(
            currentMargins.Left / 3,
            currentMargins.Right / 3,
            currentMargins.Top,
            currentMargins.Bottom);

        _pageImage = Capture(browser);
        BeginPrint += (_, _) => _sourceTop = 0;
        PrintPage += PrintNextPage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pageImage.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Captures the page with print-sized text, then restores the on-screen style.
    /// </summary>
    private static Bitmap Capture(WebBrowser browser)
    {
        HtmlElement? body = browser.Document?.Body;
        string originalStyle = body?.GetAttribute("style") ?? string.Empty;
        try
        {
            if (body is not null)
            {
                // HtmlPage uses a 15px body font; 17.25px is 15% larger for print.
                body.Style = $"{originalStyle} {PrintBodyStyle}";
            }

            return CapturePage(browser, body);
        }
        finally
        {
            if (body is not null)
            {
                body.Style = originalStyle;
            }
        }
    }

    private static Bitmap CapturePage(WebBrowser browser, HtmlElement? body)
    {
        Size size = body?.ScrollRectangle.Size ?? browser.ClientSize;
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));

        var image = new Bitmap(size.Width, size.Height);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(Color.White);

        if (body?.DomElement is not IHTMLElementRender render)
        {
            return image;
        }

        IntPtr deviceContext = graphics.GetHdc();
        try
        {
            render.DrawToDC(deviceContext);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        return image;
    }

    private void PrintNextPage(object? sender, PrintPageEventArgs e)
    {
        int marginWidth = Math.Max(1, e.MarginBounds.Width);
        int sourcePageHeight = Math.Max(1, _pageImage.Width * e.MarginBounds.Height / marginWidth);
        int sourceTop = _sourceTop;
        if (sourceTop >= _pageImage.Height)
        {
            e.HasMorePages = false;
            return;
        }

        int sourceBottom = Math.Min(sourceTop + sourcePageHeight, _pageImage.Height);
        if (sourceBottom < _pageImage.Height)
        {
            sourceBottom = FindLineBoundary(sourceTop, sourceBottom);
        }

        int sourceHeight = sourceBottom - sourceTop;
        var destination = e.MarginBounds;
        if (sourceHeight != sourcePageHeight)
        {
            // Preserve the same image scale when a page ends at a line boundary or
            // contains only the final partial page.
            destination.Height = Math.Max(1,
                (int)Math.Round((double)e.MarginBounds.Height * sourceHeight / sourcePageHeight));
        }

        e.Graphics!.DrawImage(
            _pageImage,
            destination,
            new Rectangle(0, sourceTop, _pageImage.Width, sourceHeight),
            GraphicsUnit.Pixel);

        _sourceTop = sourceBottom;
        e.HasMorePages = _sourceTop < _pageImage.Height;
    }

    /// <summary>
    /// Finds the closest scanline without foreground text before the target page
    /// boundary so rendered text lines are kept intact on one page.
    /// </summary>
    private int FindLineBoundary(int pageTop, int targetBottom)
    {
        for (int y = targetBottom - 1; y > pageTop; y--)
        {
            bool hasForeground = false;
            for (int x = 0; x < _pageImage.Width; x++)
            {
                Color pixel = _pageImage.GetPixel(x, y);
                // Ignore the light table/code backgrounds; preserve dark text,
                // muted text, links, and their antialiased edges.
                if (pixel.R < 230 && pixel.G < 230 && pixel.B < 230)
                {
                    hasForeground = true;
                    break;
                }
            }

            if (!hasForeground)
            {
                return y + 1;
            }
        }

        // Dense non-text content may have no safe scanline; keep the nominal page
        // height rather than dropping or duplicating part of the captured image.
        return targetBottom;
    }
}

[ComImport]
[Guid("3050F669-98B5-11CF-BB82-00AA00BDCE0B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHTMLElementRender
{
    void DrawToDC(IntPtr deviceContext);

    void SetDocumentPrinter([MarshalAs(UnmanagedType.BStr)] string printerName, IntPtr deviceContext);
}
