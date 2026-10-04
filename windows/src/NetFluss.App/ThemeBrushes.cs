// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// The themed palette every NetFluss window draws from: the popover, Statistics, the speed
/// test and the Network Slice share one set of resource keys, filled from one place.
///
/// <para>Card, divider, hover and the rest are derived from the theme's four colours rather
/// than carried in it: they are the same surface lifted or dropped a little, and asking
/// every theme to specify them would be a dozen more chances to leave one out.</para>
/// </summary>
internal static class ThemeBrushes
{
    internal static void Apply(ResourceDictionary target, SurfacePalette surface, ThemeColor download, ThemeColor upload)
    {
        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            target[key] = brush;
        }

        static Color Rgb(ThemeColor c) => Color.FromRgb(c.R, c.G, c.B);
        static Color Alpha(byte a, ThemeColor c) => Color.FromArgb(a, c.R, c.G, c.B);

        var dark = surface.IsDark;
        var ink = dark ? ThemeColor.FromHex("FFFFFF") : ThemeColor.FromHex("000000");
        var accent = dark ? ThemeColor.FromHex("4CC2FF") : ThemeColor.FromHex("005FB8");

        Set("PopoverBackgroundBrush", Rgb(surface.Background));
        Set("PopoverCardBrush", Rgb(surface.Card));
        Set("PopoverTextBrush", Rgb(surface.TextPrimary));
        Set("PopoverSecondaryBrush", Rgb(surface.TextSecondary));
        Set("PopoverTertiaryBrush", Alpha(0xA0, surface.TextSecondary));
        Set("PopoverDividerBrush", Alpha(dark ? (byte)0x26 : (byte)0x1A, ink));
        Set("PopoverBorderBrush", Alpha(dark ? (byte)0x33 : (byte)0x22, ink));
        Set("PopoverHoverBrush", Alpha(dark ? (byte)0x14 : (byte)0x0D, ink));
        Set("PopoverPressedBrush", Alpha(dark ? (byte)0x0A : (byte)0x08, ink));
        Set("PopoverTrackBrush", Alpha(dark ? (byte)0x1F : (byte)0x14, ink));
        Set("PopoverInputBrush", Alpha(dark ? (byte)0x1A : (byte)0x0F, ink));
        Set("PopoverAccentBrush", Rgb(accent));
        Set("PopoverAccentSoftBrush", Alpha(0x24, accent));
        Set("PopoverDownloadBrush", Rgb(download));
        Set("PopoverUploadBrush", Rgb(upload));
        Set("PopoverGreenBrush", dark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x0F, 0x7B, 0x0F));
        Set("PopoverOrangeBrush", dark ? Color.FromRgb(0xFC, 0xB7, 0x5D) : Color.FromRgb(0x9D, 0x5D, 0x00));
    }

    /// <summary>Tells DWM whether to draw the title bar and frame dark, to match the content.</summary>
    internal static void ApplyFrame(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
