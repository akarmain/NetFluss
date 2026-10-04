// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetFluss.App;

/// <summary>
/// Renders a window's content to a PNG without putting it in front of anyone.
///
/// <para>The verification harness behind <c>--snapshot</c>. A screenshot of the real screen
/// needs the window to be visible and focused, which on a machine someone is using means
/// taking their focus away every time a check runs. This lays the window out off-screen,
/// never activates it, and renders its visual tree directly — the same pixels WPF would put
/// on screen, at the window's own DPI, minus only the shell's frame shadow.</para>
/// </summary>
internal static class Snapshot
{
    /// <summary>Somewhere no monitor will ever be.</summary>
    internal const double OffScreen = -32000;

    internal static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        window.UpdateLayout();

        var handle = new WindowInteropHelper(window).Handle;
        var scale = handle == nint.Zero ? 1.0 : Screens.DpiScale(handle);
        var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * scale));

        // The window's own background is not part of its content's visual, so paint it
        // underneath — otherwise every themed window renders onto transparency.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            context.DrawRectangle(window.Background ?? Brushes.White, null, bounds);
            // An absolute viewbox: by default a VisualBrush maps only the drawn content, which
            // silently crops away any leading margin and shifts everything up and left.
            context.DrawRectangle(
                new VisualBrush(root)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = bounds,
                },
                null,
                bounds);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
