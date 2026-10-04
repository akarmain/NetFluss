// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetFluss.App.Settings;

/// <summary>
/// A list the user arranges: drag a row by its grip, or use its move buttons. Used for the
/// popover sections, the adapters and the DNS presets — every list macOS lets you drag.
///
/// <para>The move buttons are not decoration. Drag-and-drop is unreachable from the
/// keyboard and hard with a touchpad; Windows Settings offers both for the same reason.</para>
/// </summary>
internal sealed class ReorderList : StackPanel
{
    private const string DragFormat = "NetFluss.ReorderKey";

    private readonly Action<string, int> _move;
    private readonly List<string> _keys = [];
    private Point _dragOrigin;
    private string? _dragKey;

    /// <param name="move">Called with the dragged key and its new index in the full list.</param>
    internal ReorderList(Action<string, int> move) => _move = move;

    /// <summary>Replaces the rows. Each row's content is the caller's; the grip and buttons are added here.</summary>
    internal void SetRows(IEnumerable<(string Key, FrameworkElement Content)> rows)
    {
        Children.Clear();
        _keys.Clear();

        var list = rows.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var (key, content) = list[i];
            _keys.Add(key);
            Children.Add(Row(key, content, i, list.Count));
        }
    }

    private Border Row(string key, FrameworkElement content, int index, int count)
    {
        var grip = Kit.Icon("", 13, "SecondaryTextBrush");
        grip.Cursor = Cursors.SizeNS;
        grip.ToolTip = Kit.L("Drag to reorder");
        grip.Margin = new Thickness(0, 0, 10, 0);
        grip.Tag = "grip";

        var up = Kit.IconButton("", Kit.L("Move up"), () => _move(key, index - 1));
        up.IsEnabled = index > 0;
        var down = Kit.IconButton("", Kit.L("Move down"), () => _move(key, index + 1));
        down.IsEnabled = index < count - 1;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 1);
        var buttons = Kit.Row(up, down);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(grip);
        grid.Children.Add(content);
        grid.Children.Add(buttons);

        var row = new Border
        {
            Child = grid,
            Tag = key,
            AllowDrop = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 2, 0, 0),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(0, 3, 0, 3),
        };

        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Only the grip starts a drag, so a click on a switch or into a name field never
            // turns into a reorder.
            _dragKey = e.OriginalSource is TextBlock { Tag: "grip" } ? key : null;
            _dragOrigin = e.GetPosition(this);
        };

        row.MouseMove += (_, e) =>
        {
            if (_dragKey is null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            // Windows' own drag threshold, so a shaky click is not a reorder.
            var moved = e.GetPosition(this) - _dragOrigin;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var dragged = _dragKey;
            _dragKey = null;
            DragDrop.DoDragDrop(row, new DataObject(DragFormat, dragged), DragDropEffects.Move);
        };

        row.DragOver += (_, e) =>
        {
            var carrying = e.Data.GetDataPresent(DragFormat);
            e.Effects = carrying ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;

            // An insertion line, so it is clear where the row will land.
            if (carrying)
            {
                row.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            }
        };

        row.DragLeave += (_, _) => row.BorderBrush = Brushes.Transparent;

        row.Drop += (_, e) =>
        {
            row.BorderBrush = Brushes.Transparent;
            if (e.Data.GetData(DragFormat) is string dragged && dragged != key)
            {
                var target = _keys.IndexOf(key);
                if (target >= 0)
                {
                    _move(dragged, target);
                }
            }
        };

        return row;
    }
}
