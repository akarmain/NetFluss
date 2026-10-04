// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetFluss.Core;
using NetFluss.Tray;

namespace NetFluss.App.Settings;

/// <summary>
/// Taskbar — where the meter goes and what it looks like: the Windows counterpart of the
/// macOS menu bar settings in Appearance, plus the 2.6 VPN indicator.
/// </summary>
internal static class TaskbarPage
{
    /// <summary>Tray sizes at 100%, 125%, 150% and 200% scaling.</summary>
    private static readonly int[] PreviewSizes = [16, 20, 24, 32];

    /// <summary>A plausible busy moment, so the preview shows the hard case rather than "0".</summary>
    private static readonly RateTotals PreviewTotals = new(4_720_000, 834_000);

    internal static FrameworkElement Build(PreferencesContext context)
    {
        var settings = context.Settings;
        var page = new StackPanel();

        // ================================ Placement ================================
        page.Children.Add(Kit.Header(Kit.L("Where to show it")));

        var surfaceCaption = Kit.Caption(string.Empty);
        var surfaceText = new StackPanel();
        surfaceText.Children.Add(Kit.Text(Kit.L("Meter location")));
        surfaceText.Children.Add(surfaceCaption);
        page.Children.Add(Kit.Card(surfaceText, Kit.Combo(settings, nameof(AppSettings.MeterSurface),
        [
            new Choice(MeterSurface.TaskbarOverlay, Kit.L("On the taskbar")),
            new Choice(MeterSurface.Tray, Kit.L("Notification area")),
        ])));

        var fallback = Kit.Card(
            Kit.L("Showing in the notification area instead"),
            Kit.L("NetFluss could not find room on the taskbar, so the meter fell back to the notification area. It will move back on its own if the taskbar becomes available."),
            null);
        fallback.SetResourceReference(Border.BackgroundProperty, "NoticeBrush");
        page.Children.Add(fallback);

        page.Children.Add(Kit.Card(
            Kit.L("Taskbar and widget layout"),
            Kit.L("One line has room for full units; stacked matches the notification area.") + " " +
            Kit.L("Dashboard uses router-wide traffic when Fritz!Box, UniFi, OpenWRT, or OPNsense bandwidth is enabled and available."),
            Kit.Combo(settings, nameof(AppSettings.ReadoutStyle),
            [
                new Choice(ReadoutStyle.Unified, Kit.L("One line")),
                new Choice(ReadoutStyle.Stacked, Kit.L("Two lines")),
                new Choice(ReadoutStyle.Total, Kit.L("Combined total")),
                new Choice(ReadoutStyle.Dashboard, Kit.L("Dashboard")),
                new Choice(ReadoutStyle.DashboardBasic, Kit.L("Dashboard Basic")),
            ])));

        page.Children.Add(Kit.Card(
            Kit.L("Text size"),
            Kit.L("Applies to the taskbar meter and the floating widget."),
            Kit.Combo(settings, nameof(AppSettings.ReadoutFontSize),
            [
                new Choice(9.0, Kit.L("Small")),
                new Choice(11.0, Kit.L("Default")),
                new Choice(13.0, Kit.L("Large")),
                new Choice(16.0, Kit.L("Largest")),
            ])));

        var hideCaption = Kit.Caption(string.Empty);
        var hideText = new StackPanel();
        hideText.Children.Add(Kit.Text(Kit.L("Hide the notification area icon")));
        hideText.Children.Add(hideCaption);
        var hideSwitch = Kit.Switch(settings, nameof(AppSettings.HideTrayIcon));
        page.Children.Add(Kit.Card(hideText, hideSwitch));

        page.Children.Add(Kit.Card(
            Kit.L("Floating widget"),
            Kit.L("An always-on-top panel you can drag anywhere. Works alongside the other placements, and depends on nothing that a Windows update can move."),
            Kit.Switch(settings, nameof(AppSettings.ShowFloatingWidget))));

        // ============================ Notification area ============================
        page.Children.Add(Kit.Header(Kit.L("Notification area")));

        var previewCaption = Kit.Caption(string.Empty);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 2) };
        var preview = new StackPanel();
        preview.Children.Add(Kit.Text(Kit.L("Preview")));
        preview.Children.Add(previewCaption);
        preview.Children.Add(strip);
        page.Children.Add(Kit.Panel(preview));

        page.Children.Add(Kit.Card(
            Kit.L("Meter style"),
            Kit.L("A single line gets the full icon height, which is sharper at 100% scaling."),
            Kit.Combo(settings, nameof(AppSettings.MeterStyle),
            [
                new Choice(MeterStyle.TwoLine, Kit.L("Download and upload")),
                new Choice(MeterStyle.DownloadOnly, Kit.L("Download only")),
                new Choice(MeterStyle.UploadOnly, Kit.L("Upload only")),
                new Choice(MeterStyle.Icon, Kit.L("Icon only")),
            ])));

        var glyphImage = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(glyphImage, BitmapScalingMode.NearestNeighbor);
        var glyphCombo = Kit.Combo(settings, nameof(AppSettings.TrayIconGlyph),
            TrayGlyphLibrary.Options.Select(option => new Choice(option.Id, Kit.L(option.Label))), 150);
        var glyphCard = Kit.Card(
            Kit.L("Icon"),
            Kit.L("Used when the meter style is set to Icon only. Drawn to fit whatever size your display asks for."),
            Kit.Row(glyphImage, glyphCombo));
        page.Children.Add(glyphCard);

        page.Children.Add(Kit.Card(
            Kit.L("Show direction arrows"),
            Kit.L("Costs width the digits need below 24 px. The row colour already shows direction."),
            Kit.Switch(settings, nameof(AppSettings.ShowArrows))));

        page.Children.Add(Kit.Card(
            Kit.L("Keep colours readable"),
            SystemTheme.IsShellLight()
                ? Kit.L("Your taskbar is light, where saturated greens lose contrast against the blues.")
                : Kit.L("Darkens or brightens the rate colours so both rows read equally on your taskbar."),
            Kit.Switch(settings, nameof(AppSettings.EnforceContrast))));

        // ============================== VPN indicator ==============================
        page.Children.Add(Kit.Header(Kit.L("VPN indicator")));

        page.Children.Add(Kit.Card(
            Kit.L("VPN indicator"),
            Kit.L("Shown to the right of the rates on the taskbar meter and the widget. Detects VPNs started by any app, not only NetFluss."),
            Kit.Combo(settings, nameof(AppSettings.VpnIndicator),
            [
                new Choice("off", Kit.L("Off")),
                new Choice("dot", Kit.L("Dot")),
                new Choice("shield", Kit.L("Shield")),
            ], 140)));

        var colorCard = Kit.Card(
            Kit.L("VPN indicator color"),
            null,
            Kit.ColorPicker(settings, nameof(AppSettings.VpnIndicatorColor), nameof(AppSettings.VpnIndicatorColorHex), () =>
            {
                var green = ThemeColor.FromHex("2EA043");
                return AccentPalette.Resolve(settings.VpnIndicatorColor, settings.VpnIndicatorColorHex, green) ?? green;
            }));
        page.Children.Add(colorCard);

        var whenOffCard = Kit.Card(
            Kit.L("Show when VPN is off"),
            Kit.L("Keeps the indicator visible, dimmed, while no VPN is connected."),
            Kit.Switch(settings, nameof(AppSettings.VpnShowWhenOff)));
        page.Children.Add(whenOffCard);

        page.Children.Add(Kit.Card(
            Kit.L("Country flag"),
            Kit.L("Shows the country your public IP is in, looked up via ipify.org and ipwho.is. Windows has no flag glyphs, so it appears as the country code."),
            Kit.Switch(settings, nameof(AppSettings.ShowCountryFlag))));

        var renderer = new TrayMeterRenderer();

        void Refresh()
        {
            var overlayChosen = settings.MeterSurface == MeterSurface.TaskbarOverlay;
            surfaceCaption.Text = overlayChosen
                ? Kit.L("Beside the clock, with room for full units. Windows offers no supported way to do this, so it is best effort — NetFluss falls back to the notification area if the taskbar moves out from under it.")
                : Kit.L("A 16–32 px icon, depending on your display scaling. Cramped, and it never breaks.");
            fallback.Visibility = overlayChosen && NetFlussApplication.OverlayFellBackToTray ? Visibility.Visible : Visibility.Collapsed;

            hideSwitch.IsEnabled = overlayChosen;
            hideCaption.Text = overlayChosen
                ? Kit.L("The icon stays as a plain glyph so the rates are not shown twice. Hiding it leaves right-clicking the taskbar meter as the only way to reach Preferences or quit.")
                : Kit.L("Only available when the meter is on the taskbar — otherwise this is where the meter lives.");

            var iconMode = settings.MeterStyle == MeterStyle.Icon;
            glyphCard.Opacity = iconMode ? 1 : 0.6;
            glyphCombo.IsEnabled = iconMode;

            var vpnOn = settings.VpnIndicator != "off";
            colorCard.IsEnabled = whenOffCard.IsEnabled = vpnOn;
            colorCard.Opacity = whenOffCard.Opacity = vpnOn ? 1 : 0.6;

            RenderPreview(settings, renderer, strip, previewCaption, glyphImage);
        }

        Kit.Watch(page, settings, _ => Refresh());
        Refresh();
        return page;
    }

    /// <summary>
    /// The real tray bitmaps at every scaling, on the user's actual taskbar colour. The 16 px
    /// case is the whole difficulty of this port, so it is shown rather than described.
    /// </summary>
    private static void RenderPreview(AppSettings settings, TrayMeterRenderer renderer, StackPanel strip, TextBlock caption, Image glyph)
    {
        var taskbar = SystemTheme.TaskbarBackground();
        var (systemDownload, systemUpload) = SystemTheme.DefaultInk();
        var (download, upload) = settings.ResolveRateColors(systemDownload, systemUpload);
        var swatch = new SolidColorBrush(Color.FromRgb(taskbar.R, taskbar.G, taskbar.B));
        swatch.Freeze();

        caption.Text = SystemTheme.IsShellLight()
            ? Kit.L("Your taskbar is light. Shown at each display scaling.")
            : Kit.L("Your taskbar is dark. Shown at each display scaling.");

        strip.Children.Clear();
        foreach (var size in PreviewSizes)
        {
            using var bitmap = renderer.RenderBitmap(PreviewTotals, new TrayMeterOptions
            {
                Size = size,
                Layout = PreferencesWindow.ToLayout(settings.MeterStyle),
                DownloadColor = download,
                UploadColor = upload,
                UseBits = settings.UseBits,
                ShowArrows = settings.ShowArrows,
                TaskbarBackground = taskbar,
                MinimumContrastRatio = settings.EnforceContrast ? Contrast.MinimumReadableRatio : 0,
                IconGlyph = TrayGlyphLibrary.Normalize(settings.TrayIconGlyph),
            });

            var image = new Image { Source = ToImageSource(bitmap), Width = size, Height = size, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

            // Fixed-height swatches with the icons bottom-aligned: the four sizes differ by
            // 16 px, and without this the captions sit at four heights and the strip reads
            // as broken rather than as a scale comparison.
            var cell = new StackPanel { Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Bottom };
            cell.Children.Add(new Grid
            {
                Height = 52,
                Children =
                {
                    new Border
                    {
                        Background = swatch,
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(8),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Child = image,
                    },
                },
            });
            var label = Kit.Caption($"{size * 100 / 16}%");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(label);
            strip.Children.Add(cell);
        }

        using var icon = renderer.RenderBitmap(PreviewTotals, new TrayMeterOptions
        {
            Size = 32,
            Layout = TrayMeterLayout.Icon,
            IconGlyph = TrayGlyphLibrary.Normalize(settings.TrayIconGlyph),
            DownloadColor = download,
            UploadColor = upload,
            TaskbarBackground = taskbar,
            MinimumContrastRatio = settings.EnforceContrast ? Contrast.MinimumReadableRatio : 0,
        });
        glyph.Source = ToImageSource(icon);
    }

    private static BitmapSource ToImageSource(System.Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
