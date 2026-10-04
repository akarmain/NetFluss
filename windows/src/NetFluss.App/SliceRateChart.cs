// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// The Network Slice's live traffic chart — the macOS Swift Charts mirror chart: download
/// as a filled line above zero, upload below it, rate labels on the left and clock times
/// along the bottom.
/// </summary>
internal sealed class SliceRateChart : FrameworkElement
{
    private const double AxisWidth = 64;
    private const double AxisHeight = 20;

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private IReadOnlyList<(DateTime Time, double Rx, double Tx)> _points = [];
    private bool _useBits;
    private Color _download = Colors.DodgerBlue;
    private Color _upload = Colors.SeaGreen;
    private Brush _label = Brushes.Gray;
    private Pen _grid = new(new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80)), 1);
    private Pen _zero = new(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), 1);

    internal SliceRateChart()
    {
        Height = 180;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    internal void SetInk(Color download, Color upload, Brush label, Brush grid)
    {
        _download = download;
        _upload = upload;
        _label = label;
        _grid = new Pen(grid, 1);
        _grid.Freeze();
        var zero = grid.Clone();
        zero.Opacity = 1.6;
        _zero = new Pen(zero, 1);
        _zero.Freeze();
        InvalidateVisual();
    }

    internal void SetData(IReadOnlyList<(DateTime Time, double Rx, double Tx)> points, bool useBits)
    {
        _points = points;
        _useBits = useBits;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= AxisWidth + 10 || height <= AxisHeight + 10 || _points.Count < 2)
        {
            return;
        }

        var plot = new Rect(AxisWidth, 4, width - AxisWidth, height - AxisHeight - 4);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // A domain of nice steps each way; upload gets its own scale so a quiet uplink is
        // still visible under a busy downlink, the way the Mac's automatic domain behaves.
        var maxRx = Math.Max(_points.Max(p => p.Rx), 1);
        var maxTx = Math.Max(_points.Max(p => p.Tx), 1);
        var step = NiceStep((maxRx + maxTx) / 4);
        var top = Math.Ceiling(maxRx / step) * step;
        var bottom = Math.Ceiling(maxTx / step) * step;
        var span = top + bottom;

        double Y(double value) => plot.Top + ((top - value) / span * plot.Height);

        var start = _points[0].Time;
        var end = _points[^1].Time;
        var seconds = Math.Max((end - start).TotalSeconds, 1);
        double X(DateTime time) => plot.Left + ((time - start).TotalSeconds / seconds * plot.Width);

        // Grid and rate labels.
        for (var value = -bottom; value <= top + (step / 2); value += step)
        {
            var y = Math.Round(Y(value)) + 0.5;
            dc.DrawLine(Math.Abs(value) < step / 2 ? _zero : _grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Label(RateFormatter.FormatRate(Math.Abs(value), _useBits), dpi);
            dc.DrawText(text, new Point(plot.Left - text.Width - 8, Math.Clamp(y - (text.Height / 2), 0, height - AxisHeight - text.Height)));
        }

        // Clock labels.
        var culture = Localization.Culture;
        var timeFormat = culture.DateTimeFormat.LongTimePattern;
        // Up to six, but never two showing the same second while the window has just opened.
        var ticks = (int)Math.Clamp(Math.Floor(seconds) + 1, 2, 6);
        string? previous = null;
        for (var i = 0; i < ticks; i++)
        {
            var time = start.AddSeconds(seconds * i / (ticks - 1));
            var stamp = time.ToString(timeFormat, culture);
            if (stamp == previous)
            {
                continue;
            }

            previous = stamp;
            var x = X(time);
            if (i > 0 && i < ticks - 1)
            {
                dc.DrawLine(_grid, new Point(Math.Round(x) + 0.5, plot.Top), new Point(Math.Round(x) + 0.5, plot.Bottom));
            }

            var text = Label(stamp, dpi);
            var left = Math.Clamp(x - (text.Width / 2), plot.Left, width - text.Width);
            dc.DrawText(text, new Point(left, plot.Bottom + 4));
        }

        Series(dc, p => p.Rx, X, Y, _download, sign: 1);
        Series(dc, p => p.Tx, X, Y, _upload, sign: -1);
    }

    private void Series(
        DrawingContext dc,
        Func<(DateTime Time, double Rx, double Tx), double> value,
        Func<DateTime, double> x,
        Func<double, double> y,
        Color color,
        int sign)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            var first = _points[0];
            lineContext.BeginFigure(new Point(x(first.Time), y(sign * value(first))), false, false);
            areaContext.BeginFigure(new Point(x(first.Time), y(0)), true, true);
            areaContext.LineTo(new Point(x(first.Time), y(sign * value(first))), false, false);
            foreach (var point in _points.Skip(1))
            {
                var p = new Point(x(point.Time), y(sign * value(point)));
                lineContext.LineTo(p, true, true);
                areaContext.LineTo(p, false, false);
            }

            areaContext.LineTo(new Point(x(_points[^1].Time), y(0)), false, false);
        }

        line.Freeze();
        area.Freeze();

        var fill = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(color), 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    private FormattedText Label(string text, double dpi)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10, _label, dpi);

    /// <summary>1, 2 or 5 times a power of ten — gridlines at round rates.</summary>
    private static double NiceStep(double rough)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rough, 1))));
        var normalized = rough / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }
}
