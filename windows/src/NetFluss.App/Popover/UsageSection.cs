// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>
/// Data Usage: upload, download and total for today and this month, from the collected
/// history — port of the macOS <c>UsageSummarySection</c>.
/// </summary>
internal sealed class UsageSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly TextBlock[] _cells = new TextBlock[6];
    private bool _active;

    internal UsageSection(PopoverContext context)
    {
        _context = context;
        _context.Statistics.Recorded += (_, _) =>
        {
            if (_active)
            {
                Refresh();
            }
        };

        var chart = Ui.IconButton(Glyph.Chart, PopoverContext.L("Bandwidth Statistics"), () => context.Commands.ShowStatistics(), 12);
        chart.Width = 24;
        chart.Height = 24;
        chart.Margin = new Thickness(0, 4, 8, 0);

        var header = Ui.Columns(Ui.Star, Ui.Auto);
        header.Children.Add(Ui.SectionTitle(PopoverContext.L("Data Usage")).At(0));
        header.Children.Add(chart.At(1));

        var grid = new Grid { Margin = new Thickness(14, 0, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Star });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Auto });
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = Ui.Auto });
        }

        TextBlock Column(string key)
        {
            var label = Ui.Label(PopoverContext.L(key).ToUpper(Localization.Culture), 9, Ui.Secondary, FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            label.Margin = new Thickness(14, 0, 0, 4);
            return label;
        }

        grid.Children.Add(Column("Today").At(1, 0));
        grid.Children.Add(Column("This Month").At(2, 0));

        void Row(int row, string key, string? glyph, string brush, bool emphasized)
        {
            var icon = glyph is null ? new TextBlock { Width = 12 } : Ui.Icon(glyph, 10, brush);
            icon.Width = 12;
            icon.Margin = new Thickness(0, 0, 6, 0);

            var weight = emphasized ? FontWeights.SemiBold : FontWeights.Normal;
            var label = Ui.Label(PopoverContext.L(key), 12, emphasized ? Ui.Text : Ui.Secondary, weight);
            grid.Children.Add(Ui.Row(icon, label).Margin(0, 3, 0, 3).At(0, row));

            for (var column = 0; column < 2; column++)
            {
                var cell = Ui.Number("—", 12, Ui.Text, weight);
                cell.HorizontalAlignment = HorizontalAlignment.Right;
                cell.Margin = new Thickness(14, 3, 0, 3);
                grid.Children.Add(cell.At(column + 1, row));
                _cells[((row is 4 ? 3 : row) - 1) * 2 + column] = cell;
            }
        }

        Row(1, "Upload", Glyph.Up, Ui.Upload, emphasized: false);
        Row(2, "Download", Glyph.Down, Ui.Download, emphasized: false);

        var rule = Ui.Divider();
        rule.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetColumnSpan(rule, 3);
        grid.Children.Add(rule.At(0, 3));

        Row(4, "Total", null, Ui.Text, emphasized: true);

        View = Ui.Column(header, grid);
    }

    public PopoverSection Kind => PopoverSection.Usage;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        var usage = _context.Statistics.Usage();
        string F(ulong bytes) => RateFormatter.FormatBytes(bytes, Localization.Culture);

        _cells[0].Text = F(usage.Today.UploadBytes);
        _cells[1].Text = F(usage.Month.UploadBytes);
        _cells[2].Text = F(usage.Today.DownloadBytes);
        _cells[3].Text = F(usage.Month.DownloadBytes);
        _cells[4].Text = F(usage.Today.TotalBytes);
        _cells[5].Text = F(usage.Month.TotalBytes);
    }
}
