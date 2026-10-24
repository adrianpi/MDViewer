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
    private readonly Bitmap _pageImage;
    private int _pageIndex;

    public HtmlPrintDocument(WebBrowser browser)
    {
        _pageImage = Capture(browser);
        BeginPrint += (_, _) => _pageIndex = 0;
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

    private static Bitmap Capture(WebBrowser browser)
    {
        HtmlElement? body = browser.Document?.Body;
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
        int sourceTop = _pageIndex * sourcePageHeight;
        if (sourceTop >= _pageImage.Height)
        {
            e.HasMorePages = false;
            return;
        }

        int sourceHeight = Math.Min(sourcePageHeight, _pageImage.Height - sourceTop);
        var destination = e.MarginBounds;
        if (sourceHeight < sourcePageHeight)
        {
            // Draw the final partial page at the same scale as the full pages rather
            // than stretching its remaining content to the bottom margin.
            destination.Height = Math.Max(1,
                (int)Math.Round((double)e.MarginBounds.Height * sourceHeight / sourcePageHeight));
        }

        e.Graphics!.DrawImage(
            _pageImage,
            destination,
            new Rectangle(0, sourceTop, _pageImage.Width, sourceHeight),
            GraphicsUnit.Pixel);

        _pageIndex++;
        e.HasMorePages = _pageIndex * sourcePageHeight < _pageImage.Height;
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
