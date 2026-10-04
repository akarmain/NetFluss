// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>
/// The Traffic Timer — port of the macOS 2.6 <c>TrafficTimerSection</c>: a phone-stopwatch
/// face that adds up download and upload from start to stop, with the average rates and a
/// download/upload split bar.
/// </summary>
internal sealed class TimerSection : IPopoverSection
{
    private readonly PopoverContext _context;
    private readonly DispatcherTimer _clockTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Border _statePill;
    private readonly Ellipse _stateDot;
    private readonly TextBlock _stateText;
    private readonly TextBlock _clock;
    private readonly TextBlock _total;
    private readonly TextBlock _started;
    private readonly TextBlock _paused;
    private readonly Button _reset;
    private readonly Button _primary;
    private readonly TextBlock _primaryGlyph;
    private readonly Ellipse _primaryRing;
    private readonly Ellipse _primaryDisc;
    private readonly TextBlock _download;
    private readonly TextBlock _downloadAverage;
    private readonly TextBlock _upload;
    private readonly TextBlock _uploadAverage;
    private readonly Grid _shareBar;
    private readonly ColumnDefinition _downloadShare;
    private readonly ColumnDefinition _uploadShare;
    private readonly Border _downloadFill;
    private readonly Border _uploadFill;
    private readonly Border _emptyFill;
    private bool _active;

    internal TimerSection(PopoverContext context)
    {
        _context = context;
        _context.Timer.Changed += (_, _) =>
        {
            if (_active)
            {
                Refresh();
            }
        };
        _clockTick.Tick += (_, _) => Refresh();

        // Header: stopwatch, title, and a state pill once the timer has been used.
        var titleIcon = Ui.Icon("", 11, Ui.Secondary).Margin(0, 0, 5, 0);
        var title = Ui.Label(PopoverContext.L("Traffic Timer"), 11, Ui.Secondary, FontWeights.SemiBold);

        _stateDot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _stateText = Ui.Label(string.Empty, 10, Ui.Secondary, FontWeights.Medium);
        _statePill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 1),
            Child = Ui.Row(_stateDot, _stateText),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statePill.SetResourceReference(Border.BackgroundProperty, "PopoverTrackBrush");

        var header = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
        header.Margin = new Thickness(14, 10, 14, 0);
        header.Children.Add(Ui.Row(titleIcon, title).At(0));
        header.Children.Add(_statePill.At(2));

        // Clock row: reset on the left, play/pause on the right, as on a phone stopwatch.
        _reset = RoundButton("", Ui.Secondary, PopoverContext.L("Reset"), () => _context.Timer.Reset(), out _, out _, out _);
        _primary = RoundButton("", Ui.Green, PopoverContext.L("Start"), Toggle, out _primaryGlyph, out _primaryRing, out _primaryDisc);

        _clock = Ui.Number("00:00", 36, Ui.Text, FontWeights.Light);
        _clock.HorizontalAlignment = HorizontalAlignment.Center;
        _total = Ui.Number(string.Empty, 10, Ui.Secondary, FontWeights.Medium);
        _total.HorizontalAlignment = HorizontalAlignment.Center;
        _started = Ui.Number(string.Empty, 10, Ui.Secondary);
        _started.HorizontalAlignment = HorizontalAlignment.Center;
        _started.Margin = new Thickness(0, 3, 0, 0);
        _paused = Ui.Number(string.Empty, 10, Ui.Secondary);
        _paused.HorizontalAlignment = HorizontalAlignment.Center;

        var face = Ui.Column(_clock, _total, _started, _paused);
        face.VerticalAlignment = VerticalAlignment.Center;

        var clockRow = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
        clockRow.Margin = new Thickness(18, 6, 18, 0);
        clockRow.Children.Add(_reset.At(0));
        clockRow.Children.Add(face.At(1));
        clockRow.Children.Add(_primary.At(2));

        // Traffic cells.
        (_download, _downloadAverage, var downCell) = Cell(Glyph.Down, Ui.Download, PopoverContext.L("Download"));
        (_upload, _uploadAverage, var upCell) = Cell(Glyph.Up, Ui.Upload, PopoverContext.L("Upload"));

        var cells = Ui.Columns(Ui.Star, Ui.Auto, Ui.Star);
        cells.Margin = new Thickness(0, 10, 0, 0);
        cells.Children.Add(downCell.At(0));
        var divider = new Border { Width = 1, Height = 34 };
        divider.SetResourceReference(Border.BackgroundProperty, "PopoverDividerBrush");
        cells.Children.Add(divider.At(1));
        cells.Children.Add(upCell.At(2));

        // Download/upload split bar.
        _downloadShare = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _uploadShare = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _shareBar = new Grid { Height = 3, Margin = new Thickness(14, 9, 14, 12) };
        _shareBar.ColumnDefinitions.Add(_downloadShare);
        _shareBar.ColumnDefinitions.Add(_uploadShare);

        _downloadFill = new Border { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 1, 0) };
        _downloadFill.SetResourceReference(Border.BackgroundProperty, Ui.Download);
        _uploadFill = new Border { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1, 0, 0, 0) };
        _uploadFill.SetResourceReference(Border.BackgroundProperty, Ui.Upload);
        _emptyFill = new Border { CornerRadius = new CornerRadius(1.5) };
        _emptyFill.SetResourceReference(Border.BackgroundProperty, "PopoverTrackBrush");
        Grid.SetColumnSpan(_emptyFill, 2);

        _shareBar.Children.Add(_downloadFill.At(0));
        _shareBar.Children.Add(_uploadFill.At(1));
        _shareBar.Children.Add(_emptyFill);

        View = Ui.Column(header, clockRow, cells, _shareBar);
    }

    public PopoverSection Kind => PopoverSection.Timer;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Refresh();
        }
        else
        {
            _clockTick.Stop();
        }
    }

    public void Refresh()
    {
        var timer = _context.Timer;
        var culture = Localization.Culture;
        var elapsed = timer.Elapsed();
        var running = timer.State == TrafficTimerState.Running;

        // The face ticks once a second only while it is both on screen and counting.
        if (_active && running && !_clockTick.IsEnabled)
        {
            _clockTick.Start();
        }
        else if ((!_active || !running) && _clockTick.IsEnabled)
        {
            _clockTick.Stop();
        }

        _statePill.Visibility = timer.State == TrafficTimerState.Idle ? Visibility.Collapsed : Visibility.Visible;
        _stateDot.SetResourceReference(Shape.FillProperty, running ? Ui.Green : Ui.Orange);
        _stateText.Text = PopoverContext.L(running ? "Running" : "Paused");

        _clock.Text = TrafficTimer.ClockText(elapsed);
        _clock.Opacity = timer.State == TrafficTimerState.Paused ? 0.55 : 1;
        _total.Text = $"{PopoverContext.L("Total").ToUpper(culture)}  {Bytes(timer.TotalBytes)}";

        _started.Text = timer.StartedAt is { } started
            ? $"{PopoverContext.L("Starting time")}: {started.LocalDateTime.ToString("g", culture)}"
            : string.Empty;
        _started.Visibility = timer.StartedAt is null ? Visibility.Collapsed : Visibility.Visible;
        _paused.Text = timer.State == TrafficTimerState.Paused && timer.PausedAt is { } paused
            ? $"{PopoverContext.L("Paused at")}: {paused.LocalDateTime.ToString("g", culture)}"
            : string.Empty;
        _paused.Visibility = _paused.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        _reset.IsEnabled = timer.State != TrafficTimerState.Idle;
        _reset.Opacity = _reset.IsEnabled ? 1 : 0.35;

        var tint = running ? Ui.Orange : Ui.Green;
        _primaryGlyph.Text = running ? "" : "";
        _primaryGlyph.SetResourceReference(TextBlock.ForegroundProperty, tint);
        _primaryRing.SetResourceReference(Shape.StrokeProperty, tint);
        _primaryDisc.SetResourceReference(Shape.FillProperty, tint);
        _primary.ToolTip = PopoverContext.L(timer.State switch
        {
            TrafficTimerState.Running => "Pause",
            TrafficTimerState.Paused => "Resume",
            _ => "Start",
        });

        var seconds = elapsed.TotalSeconds;
        var useBits = _context.UseBits;
        _download.Text = Bytes(timer.DownloadBytes);
        _upload.Text = Bytes(timer.UploadBytes);
        _downloadAverage.Text = "⌀ " + RateFormatter.FormatRate(seconds >= 1 ? timer.DownloadBytes / seconds : 0, useBits);
        _uploadAverage.Text = "⌀ " + RateFormatter.FormatRate(seconds >= 1 ? timer.UploadBytes / seconds : 0, useBits);

        var total = (double)timer.TotalBytes;
        var hasTraffic = total > 0;
        _emptyFill.Visibility = hasTraffic ? Visibility.Collapsed : Visibility.Visible;
        _downloadFill.Visibility = hasTraffic && timer.DownloadBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        _uploadFill.Visibility = hasTraffic && timer.UploadBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        _downloadShare.Width = new GridLength(hasTraffic ? Math.Max(timer.DownloadBytes / total, 0.0001) : 1, GridUnitType.Star);
        _uploadShare.Width = new GridLength(hasTraffic ? Math.Max(timer.UploadBytes / total, 0.0001) : 1, GridUnitType.Star);
    }

    private void Toggle()
    {
        _context.Timer.Toggle();
        Refresh();
    }

    private static string Bytes(ulong bytes) => RateFormatter.FormatBytes(bytes, Localization.Culture);

    private static (TextBlock Amount, TextBlock Average, FrameworkElement Cell) Cell(string glyph, string brush, string label)
    {
        var icon = Ui.Icon(glyph, 14, brush).Margin(0, 0, 8, 0);
        var caption = Ui.Label(label.ToUpper(Localization.Culture), 9, Ui.Secondary, FontWeights.SemiBold);
        var amount = Ui.Number("—", 14, Ui.Text, FontWeights.SemiBold);
        var average = Ui.Number("—", 9, Ui.Secondary);

        var row = Ui.Row(icon, Ui.Column(caption, amount, average));
        row.HorizontalAlignment = HorizontalAlignment.Center;
        return (amount, average, row);
    }

    /// <summary>A phone-stopwatch button: a tinted disc inside a thin ring, pressed smaller.</summary>
    private static Button RoundButton(string glyph, string tint, string tooltip, Action onClick, out TextBlock icon, out Ellipse ring, out Ellipse disc)
    {
        ring = new Ellipse { StrokeThickness = 1.2, Opacity = 0.45 };
        ring.SetResourceReference(Shape.StrokeProperty, tint);

        disc = new Ellipse { Margin = new Thickness(3.5), Opacity = 0.2 };
        disc.SetResourceReference(Shape.FillProperty, tint);

        icon = Ui.Icon(glyph, 15, tint);

        var face = new Grid { Width = 46, Height = 46, Background = Brushes.Transparent };
        face.Children.Add(ring);
        face.Children.Add(disc);
        face.Children.Add(icon);

        var button = new Button
        {
            Content = face,
            ToolTip = tooltip,
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = Plain(),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };

        button.PreviewMouseLeftButtonDown += (_, _) => ((ScaleTransform)button.RenderTransform).ScaleX = ((ScaleTransform)button.RenderTransform).ScaleY = 0.93;
        button.PreviewMouseLeftButtonUp += (_, _) => ((ScaleTransform)button.RenderTransform).ScaleX = ((ScaleTransform)button.RenderTransform).ScaleY = 1;
        button.MouseLeave += (_, _) => ((ScaleTransform)button.RenderTransform).ScaleX = ((ScaleTransform)button.RenderTransform).ScaleY = 1;
        button.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return button;
    }

    private static ControlTemplate Plain()
    {
        var template = new ControlTemplate(typeof(Button));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return template;
    }
}
