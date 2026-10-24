using System;
using System.Windows.Forms;

namespace Md2Html;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The application uses only classes from the .NET runtime, including the
        // legacy WebBrowser control. No NuGet packages or native SDKs are needed.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // WebBrowser is Internet Explorer based. Without this opt-in it may render
        // in IE7 compatibility mode, which would destroy the layout of the page.
        BrowserEmulation.EnableModernMode();

        // A path supplied on the command line is opened immediately. Everything
        // after the executable is treated as the path, so quoted paths and spaces
        // work exactly as they do for any other Windows application.
        string? initialFile = args.Length > 0 ? args[0] : null;
        Application.Run(new MainForm(initialFile));
    }
}
