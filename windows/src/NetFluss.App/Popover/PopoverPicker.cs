// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NetFluss.App.Popover;

/// <summary>
/// A themed drop-down for the popover: the current choice with a chevron, opening a flyout
/// list — the SwiftUI menu-style Picker. A stock ComboBox carries the Windows 7 chrome into
/// every theme, which is why the popover never uses one.
/// </summary>
internal sealed class PopoverPicker
{
    private readonly TextBlock _label = Ui.Label(string.Empty, 12, Ui.Text, FontWeights.Medium);
    private readonly Popup _popup;
    private readonly StackPanel _list = new();
    private readonly Button _button;
    private IReadOnlyList<string> _items = [];

    internal PopoverPicker(Action<int> selected)
    {
        Selected = selected;
        var chevron = Ui.Icon(Glyph.ChevronDown, 9, Ui.Secondary).Margin(8, 0, 0, 0);
        var content = Ui.Columns(Ui.Star, Ui.Auto);
        content.Children.Add(_label.At(0));
        content.Children.Add(chevron.At(1));

        _button = Ui.RowButton(content, Open);
        _button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _button.Padding = new Thickness(8, 4, 8, 4);
        _button.SetResourceReference(Control.BackgroundProperty, "PopoverCardBrush");

        _popup = new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            PlacementTarget = _button,
            Child = Flyout.Frame(new ScrollViewer { Content = _list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 260),
        };

        View = new Grid { Children = { _button, _popup } };
    }

    internal FrameworkElement View { get; }

    private Action<int> Selected { get; }

    internal bool IsEnabled
    {
        set => _button.IsEnabled = value;
    }

    internal void Set(IReadOnlyList<string> items, int selected)
    {
        _items = items;
        _label.Text = selected >= 0 && selected < items.Count ? items[selected] : string.Empty;
        _selectedIndex = selected;
    }

    private int _selectedIndex;

    private void Open()
    {
        _list.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var check = Ui.Icon(i == _selectedIndex ? Glyph.Check : string.Empty, 11, Ui.Accent);
            check.Width = 18;
            var row = Ui.RowButton(Ui.Row(check, Ui.Label(_items[i], 12)), () =>
            {
                _popup.IsOpen = false;
                Selected(index);
            });
            row.HorizontalContentAlignment = HorizontalAlignment.Left;
            row.Padding = new Thickness(4, 5, 8, 5);
            _list.Children.Add(row);
        }

        if (_popup.Child is FrameworkElement frame)
        {
            frame.Width = Math.Max(200, _button.ActualWidth + 16);
        }

        _popup.IsOpen = true;
    }
}
