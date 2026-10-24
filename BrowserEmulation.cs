using System;
using System.IO;
using Microsoft.Win32;

namespace Md2Html;

/// <summary>
/// Opts this executable into the newest installed Internet Explorer rendering mode.
/// The WinForms <see cref="System.Windows.Forms.WebBrowser"/> control is a thin wrapper
/// around MSHTML and otherwise defaults to IE7 emulation for unknown applications.
/// </summary>
internal static class BrowserEmulation
{
    private const string FeatureControlKey =
        @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";

    // 11001 = IE11 edge mode, ignoring the (legacy) document mode meta tag if possible.
    private const int Ie11EdgeMode = 11001;

    public static void EnableModernMode()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(FeatureControlKey);

            // The apphost name is what the CLR reports to the browser control.
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "Md2Html.exe");
            key.SetValue(exeName, Ie11EdgeMode, RegistryValueKind.DWord);
        }
        catch
        {
            // The registry is a per-user performance/rendering hint. If it cannot be
            // written (locked-down machine), the document still renders, just with the
            // browser's default compatibility mode.
        }
    }
}
