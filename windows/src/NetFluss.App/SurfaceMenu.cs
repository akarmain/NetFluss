// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Windows.Controls;
using NetFluss.App.Popover;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// The command menu, identical on every surface the meter can appear on, and in the
/// popover's "More" button.
///
/// <para><b>Why this is centralised.</b> Whichever surface a user has chosen is, for them,
/// the whole application — it is the only part of NetFluss on screen. If the taskbar overlay
/// carries the meter and only the tray icon has a menu, then hiding the tray icon locks the
/// user out of their own preferences and, worse, out of quitting: Task Manager becomes the
/// only way to stop the app. Building one menu in one place is what stops a surface from
/// being added later without one.</para>
///
/// <para>The items and their order are the macOS status-item menu's. "Try FileFluss" is left
/// out: FileFluss is a Mac app, and offering it here would send a Windows user to a download
/// they cannot run.</para>
/// </summary>
internal static class SurfaceMenu
{
    private const string SupportUrl = "https://buymeacoffee.com/robertrudolph";

    /// <summary>
    /// A fresh menu each call. WPF menus carry placement state tied to the element that
    /// opened them, so sharing one instance across the overlay and the widget would have
    /// the second one open in the first one's position.
    /// </summary>
    internal static ContextMenu Build(AppCommands commands)
    {
        var menu = new ContextMenu();

        void Add(string key, Action action)
        {
            var item = new MenuItem { Header = Localization.L(key) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Preferences", commands.ShowPreferences);
        Add("Bandwidth Statistics", commands.ShowStatistics);
        Add("Speed Test…", commands.ShowSpeedTest);
        Add("Network Slice", commands.ShowNetworkSlice);
        Add("About NetFluss", commands.ShowAbout);
        Add("Copy Network Diagnostics…", commands.CopyDiagnostics);
        Add("Support NetFluss project", OpenSupport);
        menu.Items.Add(new Separator());
        Add("Quit NetFluss", commands.Quit);

        return menu;
    }

    private static void OpenSupport()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser registered; nothing useful to do.
        }
    }
}
