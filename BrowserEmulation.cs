using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MDViewer;

/// <summary>
/// Optionally opts this executable into the newest Internet Explorer rendering mode.
/// The WinForms <see cref="WebBrowser"/> control is a thin wrapper around MSHTML and
/// can otherwise default to its legacy IE7 emulation. The registry change is never
/// made silently: the user is asked first, and the viewer works without it.
/// </summary>
internal static class BrowserEmulation
{
    private const string FeatureControlKey =
        @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";

    // 11001 = IE11 edge mode, ignoring the (legacy) document mode meta tag if possible.
    private const int Ie11EdgeMode = 11001;

    // 11000 is also IE11; any value at least this high is already modern enough.
    private const int MinimumModernMode = 11000;

    public static void ConfigureRenderingMode()
    {
        string? exeName = GetExecutableName();
        if (exeName is null)
        {
            // The document still has an IE=edge meta tag. This only means we cannot
            // safely target this executable with a per-application registry value.
            return;
        }

        int? configuredMode = ReadConfiguredMode(exeName);
        if (configuredMode >= MinimumModernMode)
        {
            // No registry write is needed, so there is nothing to ask about.
            return;
        }

        DialogResult answer = MessageBox.Show(
            "Markdown Viewer uses the Windows Internet Explorer web view.\n\n" +
            "To enable modern HTML rendering, it can configure this per-user setting:\n\n" +
            "HKEY_CURRENT_USER\\Software\\Microsoft\\\n" +
            "Internet Explorer\\Main\\FeatureControl\\\n" +
            "FEATURE_BROWSER_EMULATION\n" +
            exeName + " = " + Ie11EdgeMode + "\n\n" +
            "This affects only Markdown Viewer for the current Windows user.\n\n" +
            "Allow the change?\n\n" +
            "If you choose No, no registry change is made. The viewer will use " +
            "compatibility mode; documents still open and links still work, but some " +
            "styling may be simplified.",
            "Browser rendering mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(FeatureControlKey);
            key.SetValue(exeName, Ie11EdgeMode, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       SecurityException or
                                       IOException)
        {
            // Consent was given, but the machine may be locked down. Fail soft and
            // stay with the compatibility mode instead of preventing startup.
            MessageBox.Show(
                "The browser rendering setting could not be saved:\n\n" + ex.Message +
                "\n\nMarkdown Viewer will continue in compatibility mode.",
                "Browser rendering mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static string? GetExecutableName()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        string exeName = Path.GetFileName(processPath);
        if (string.Equals(exeName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Setting a browser-emulation value for dotnet.exe would affect every
            // .NET application hosted by that process, so leave it alone.
            return null;
        }

        return exeName;
    }

    private static int? ReadConfiguredMode(string exeName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(FeatureControlKey, writable: false);
            object? value = key?.GetValue(exeName);
            return value switch
            {
                int number => number,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int number) => number,
                _ => null
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       SecurityException or
                                       IOException)
        {
            // If it cannot be read, treat it as unset and let the user decide. A
            // later write failure is reported separately.
            return null;
        }
    }
}
