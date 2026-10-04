// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetFluss.App;

/// <summary>
/// The NetFluss icon for window title bars, the taskbar and Alt+Tab. Converted from the
/// macOS AppIcon.icns with its icon-grid margin cropped away — a Windows icon fills its
/// square, and the Mac's padding would make the 16–32 px sizes look shrunken.
/// </summary>
internal static class AppIcon
{
    private static ImageSource? _icon;

    internal static ImageSource Source =>
        _icon ??= BitmapFrame.Create(new Uri("pack://application:,,,/NetFluss;component/Assets/NetFluss.ico"));

    internal static void Apply(Window window) => window.Icon = Source;
}
