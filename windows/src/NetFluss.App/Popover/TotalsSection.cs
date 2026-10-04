// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>The Download / Upload header — port of the macOS <c>TotalRatesHeader</c>.</summary>
internal sealed class TotalsSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly TextBlock _download;
    private readonly TextBlock _upload;

    internal TotalsSection(PopoverContext context)
    {
        _context = context;

        var grid = Ui.Columns(Ui.Star, Ui.Auto, Ui.Star);
        grid.Margin = new Thickness(0, 10, 0, 10);

        _download = Ui.Number("—", 15, Ui.Text, FontWeights.SemiBold);
        _upload = Ui.Number("—", 15, Ui.Text, FontWeights.SemiBold);

        grid.Children.Add(Cell(Glyph.Down, Ui.Download, PopoverContext.L("Download"), _download).At(0));

        var divider = new Border { Width = 1, Margin = new Thickness(0, 2, 0, 2) };
        divider.SetResourceReference(Border.BackgroundProperty, "PopoverDividerBrush");
        grid.Children.Add(divider.At(1));

        grid.Children.Add(Cell(Glyph.Up, Ui.Upload, PopoverContext.L("Upload"), _upload).At(2));

        View = grid;
    }

    public PopoverSection Kind => PopoverSection.Totals;

    public FrameworkElement View { get; }

    public void Refresh()
    {
        var totals = _context.Monitor.Totals;
        _download.Text = RateFormatter.FormatRate(totals.RxRateBps, _context.UseBits);
        _upload.Text = RateFormatter.FormatRate(totals.TxRateBps, _context.UseBits);
    }

    public void SetActive(bool active)
    {
    }

    private static FrameworkElement Cell(string glyph, string brush, string label, TextBlock value)
    {
        var caption = Ui.Label(label.ToUpper(System.Globalization.CultureInfo.CurrentCulture), 10, Ui.Secondary, FontWeights.SemiBold);
        caption.Margin = new Thickness(0, 0, 0, 1);

        var icon = Ui.Icon(glyph, 15, brush);
        icon.Margin = new Thickness(0, 0, 9, 0);

        var row = Ui.Row(icon, Ui.Column(caption, value));
        row.HorizontalAlignment = HorizontalAlignment.Center;
        return row;
    }
}
