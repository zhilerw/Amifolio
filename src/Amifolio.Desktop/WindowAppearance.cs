using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

internal static class WindowAppearance
{
    private const int CornerPreferenceAttribute = 33;
    private const int RoundSmall = 3;

    internal static int? ApplySmallCorners(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return null;
        var preference = RoundSmall;
        return DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window),
            CornerPreferenceAttribute, ref preference, sizeof(int));
    }

    internal static int? ReadCornerPreference(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return null;
        var result = DwmGetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window),
            CornerPreferenceAttribute, out var preference, sizeof(int));
        return result >= 0 ? preference : null;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
