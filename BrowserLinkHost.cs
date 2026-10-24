using System.Runtime.InteropServices;

namespace MDViewer;

/// <summary>
/// The object exposed to document JavaScript as <c>window.external</c>.
/// It exists only to forward link clicks to the form. The WebBrowser is hosted
/// on an <c>about:blank</c> document, where IE does not raise Navigating for
/// local <c>file:///</c> links, so the page's click handler is the reliable path.
/// </summary>
[ComVisible(true)]
public sealed class BrowserLinkHost
{
    private readonly MainForm _form;

    public BrowserLinkHost(MainForm form)
    {
        _form = form;
    }

    /// <summary>
    /// Called from the page's click handler. The absolute URI is supplied by the
    /// browser (and therefore already resolved against the document's base URI).
    /// </summary>
    public void OpenLink(string rawHref, string absoluteHref)
    {
        _form.HandleLink(rawHref, absoluteHref);
    }
}
