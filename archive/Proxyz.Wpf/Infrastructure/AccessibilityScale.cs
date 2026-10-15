using Microsoft.Win32;
using System.Windows;

namespace Proxyz.App.Infrastructure;

public static class AccessibilityScale
{
    public static void Apply(double applicationScale)
    {
        double systemScale = 1;
        // TextScaleFactor is a read-only compatibility bridge for classic WPF.
        // Its registry representation is not a public contractual API: fall back to
        // SystemFonts and the explicit in-app scale, and test on each target OS.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            if (key?.GetValue("TextScaleFactor") is int factor && factor is >= 100 and <= 300) systemScale = factor / 100.0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { }
        double body = Math.Max(15, SystemFonts.MessageFontSize) * systemScale * applicationScale;
        Application.Current.Resources["BodyFontSize"] = body;
        Application.Current.Resources["HeadingFontSize"] = body * 1.8;
        Application.Current.Resources["SectionFontSize"] = body * 1.32;
    }
}
