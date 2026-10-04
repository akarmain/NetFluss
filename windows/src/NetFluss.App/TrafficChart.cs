// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetFluss.Core;

namespace NetFluss.App;

/// <summary>
/// The Statistics window's timeline: bars with a fading gradient, a smoothed line along
/// their tops and a dashed rule at the peak — the macOS Swift Charts composition (BarMark +
/// LineMark + RuleMark), drawn directly so it costs one render pass and no chart library.
///
/// <para>Hovering a bar shows its bucket and value, which the Mac chart does not do and a
/// chart of hundreds of bars badly needs.</para>
/// </summary>
internal sealed class TrafficChart : FrameworkElement
{
    private const double AxisWidth = 58;
    private const double AxisHeight = 22;

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private IReadOnlyList<(DateTime Date, double Bytes)> _points = [];
    private TimelineGranularity _granularity;
    private Color _color = Colors.DodgerBlue;
    private Brush _label = Brushes.Gray;
    private Brush _grid = new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80));
    private Brush _tooltipBackground = Brushes.Black;
    private Brush _tooltipText = Brushes.White;
    private int _hover = -1;

    internal TrafficChart()
    {
        Height = 190;
        ClipToBounds = true;
        MouseMove += (_, e) => UpdateHover(e.GetPosition(this));
        MouseLeave += (_, _) =>
        {
            _hover = -1;
            InvalidateVisual();
        };
    }

    internal void SetData(IReadOnlyList<(DateTime Date, double Bytes)> points, TimelineGranularity granularity, Color color)
    {
        _points = points;
        _granularity = granularity;
        _color = color;
        _hover = -1;
        InvalidateVisual();
    }

    internal void SetInk(Brush label, Brush grid, Brush tooltipBackground, Brush tooltipText)
    {
        _label = label;
        _grid = grid;
        _tooltipBackground = tooltipBackground;
        _tooltipText = tooltipText;
        InvalidateVisual();
    }

    private Rect Plot => new(0, 6, Math.Max(0, ActualWidth - AxisWidth), Math.Max(0, ActualHeight - AxisHeight - 6));

    protected override void OnRender(DrawingContext context)
    {
        // A transparent fill so the whole area hit-tests for hover, not just the bars.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var plot = Plot;
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        var peak = _points.Count == 0 ? 0 : _points.Max(p => p.Bytes);
        var top = NiceCeiling(Math.Max(peak * 1.18, 1_000_000));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(_grid, 1);
        gridPen.Freeze();

        // Horizontal grid with byte labels on the right, as the Mac chart puts them.
        for (var i = 0; i <= 4; i++)
        {
            var value = top * i / 4;
            var y = Math.Round(plot.Bottom - (plot.Height * i / 4)) + 0.5;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var text = Text(AxisLabel(value), 11, _label, dpi);
            context.DrawText(text, new Point(plot.Right + 8, y - (text.Height / 2)));
        }

        if (_points.Count == 0)
        {
            return;
        }

        var slot = plot.Width / _points.Count;
        var barWidth = Math.Max(1, Math.Min(slot * 0.72, 28));

        var gradient = new LinearGradientBrush(
            Color.FromArgb(0xEB, _color.R, _color.G, _color.B),
            Color.FromArgb(0x52, _color.R, _color.G, _color.B),
            90);
        gradient.Freeze();

        var highlight = new SolidColorBrush(_color);
        highlight.Freeze();

        var tops = new List<Point>(_points.Count);
        for (var i = 0; i < _points.Count; i++)
        {
            var height = top <= 0 ? 0 : plot.Height * (_points[i].Bytes / top);
            var x = plot.Left + (slot * i) + ((slot - barWidth) / 2);
            var y = plot.Bottom - height;

            if (height > 0.5)
            {
                var radius = Math.Min(3, barWidth / 3);
                context.DrawRoundedRectangle(i == _hover ? highlight : gradient, null, new Rect(x, y, barWidth, height), radius, radius);
            }

            tops.Add(new Point(x + (barWidth / 2), y));
        }

        // The line over the bar tops, smoothed with a Catmull-Rom spline as the Mac's is.
        if (tops.Count > 1)
        {
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(tops[0], false, false);
                for (var i = 0; i < tops.Count - 1; i++)
                {
                    var p0 = tops[Math.Max(0, i - 1)];
                    var p1 = tops[i];
                    var p2 = tops[i + 1];
                    var p3 = tops[Math.Min(tops.Count - 1, i + 2)];

                    var c1 = new Point(p1.X + ((p2.X - p0.X) / 6), Math.Min(plot.Bottom, p1.Y + ((p2.Y - p0.Y) / 6)));
                    var c2 = new Point(p2.X - ((p3.X - p1.X) / 6), Math.Min(plot.Bottom, p2.Y - ((p3.Y - p1.Y) / 6)));
                    g.BezierTo(c1, c2, p2, true, true);
                }
            }

            line.Freeze();
            var pen = new Pen(highlight, 1.6) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            context.DrawGeometry(null, pen, line);
        }

        // Dashed rule at the peak.
        if (peak > 0)
        {
            var peakY = Math.Round(plot.Bottom - (plot.Height * (peak / top))) + 0.5;
            var dash = new Pen(new SolidColorBrush(Color.FromArgb(0x40, _color.R, _color.G, _color.B)), 1) { DashStyle = new DashStyle([4, 4], 0) };
            dash.Freeze();
            context.DrawLine(dash, new Point(plot.Left, peakY), new Point(plot.Right, peakY));
        }

        DrawTimeAxis(context, plot, slot, dpi);

        if (_hover >= 0 && _hover < _points.Count)
        {
            DrawTooltip(context, plot, slot, dpi);
        }
    }

    private void DrawTimeAxis(DrawingContext context, Rect plot, double slot, double dpi)
    {
        var desired = _granularity switch
        {
            TimelineGranularity.Day when _points.Count <= 7 => _points.Count,
            TimelineGranularity.Day => 5,
            _ => 6,
        };

        var step = Math.Max(1, (int)Math.Ceiling(_points.Count / (double)Math.Max(1, desired)));
        double lastRight = double.MinValue;

        for (var i = 0; i < _points.Count; i += step)
        {
            var text = Text(TimeLabel(_points[i].Date), 11, _label, dpi);
            var x = plot.Left + (slot * i) + (slot / 2) - (text.Width / 2);
            x = Math.Clamp(x, plot.Left, Math.Max(plot.Left, plot.Right - text.Width));

            // Never let two labels overlap, whatever the window width.
            if (x < lastRight + 6)
            {
                continue;
            }

            context.DrawText(text, new Point(x, plot.Bottom + 5));
            lastRight = x + text.Width;
        }
    }

    private void DrawTooltip(DrawingContext context, Rect plot, double slot, double dpi)
    {
        var point = _points[_hover];
        var label = $"{TooltipDate(point.Date)}  ·  {RateFormatter.FormatBytes((ulong)point.Bytes, Localization.Culture)}";
        var text = Text(label, 12, _tooltipText, dpi);

        var width = text.Width + 16;
        var height = text.Height + 8;
        var centre = plot.Left + (slot * _hover) + (slot / 2);
        var x = Math.Clamp(centre - (width / 2), 0, Math.Max(0, ActualWidth - width));
        var box = new Rect(x, 0, width, height);

        context.PushOpacity(0.94);
        context.DrawRoundedRectangle(_tooltipBackground, null, box, 5, 5);
        context.Pop();
        context.DrawText(text, new Point(box.Left + 8, box.Top + 4));
    }

    private void UpdateHover(Point position)
    {
        var plot = Plot;
        var index = -1;
        if (_points.Count > 0 && plot.Contains(position with { Y = Math.Clamp(position.Y, plot.Top, plot.Bottom) }))
        {
            index = Math.Clamp((int)((position.X - plot.Left) / (plot.Width / _points.Count)), 0, _points.Count - 1);
        }

        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    private string TimeLabel(DateTime date)
    {
        var culture = Localization.Culture;
        return _granularity switch
        {
            TimelineGranularity.Minute => date.ToString("t", culture),
            TimelineGranularity.Hour => date.ToString(culture.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH" : "h tt", culture),
            TimelineGranularity.Day => date.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), culture),
            _ => date.ToString("MMM", culture),
        };
    }

    private string TooltipDate(DateTime date)
    {
        var culture = Localization.Culture;
        return _granularity switch
        {
            TimelineGranularity.Minute => date.ToString("t", culture),
            TimelineGranularity.Hour => $"{date.ToString("d", culture)} {date.ToString("t", culture)}",
            TimelineGranularity.Day => date.ToString("D", culture),
            _ => date.ToString("Y", culture),
        };
    }

    /// <summary>
    /// Axis values in the unit that keeps them short and exact: a quarter of 1 MB reads
    /// "250 KB", not a rounded "0.3 MB" that disagrees with the gridline it labels.
    /// </summary>
    private static string AxisLabel(double bytes)
    {
        var culture = Localization.Culture;
        if (bytes <= 0)
        {
            return "0";
        }

        return bytes switch
        {
            >= 1e12 => (bytes / 1e12).ToString("0.##", culture) + " TB",
            >= 1e9 => (bytes / 1e9).ToString("0.##", culture) + " GB",
            >= 1e6 => (bytes / 1e6).ToString("0.##", culture) + " MB",
            _ => (bytes / 1e3).ToString("0.##", culture) + " KB",
        };
    }

    /// <summary>A round axis maximum, so the gridlines land on readable values.</summary>
    private static double NiceCeiling(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 1.2, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0 })
        {
            if (step * magnitude >= value)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private static FormattedText Text(string text, double size, Brush brush, double dpi)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, size, brush, dpi);
}
