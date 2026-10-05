// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using NetFluss.Core;

namespace NetFluss.App.Popover;

/// <summary>
/// The five busiest apps, live while the popover is open — port of the macOS
/// <c>TopAppsSection</c>, including hiding a noisy app straight from its row.
/// </summary>
internal sealed class TopAppsSection : IPopoverSection
{
    /// <summary>Fixed, as on macOS, so the popover does not jump as apps come and go.</summary>
    private const double ListHeight = 176;

    private readonly PopoverContext _context;
    private readonly TopAppsAggregator _aggregator = new();
    private readonly StackPanel _rows = new() { Margin = new Thickness(8, 0, 8, 8) };
    private readonly TextBlock _placeholder;
    private readonly StackPanel _needsHelper;
    private readonly TextBlock _helperText;
    private readonly Button _helperButton;
    private readonly List<AppRow> _rowPool = [];
    private IDisposable? _lease;
    private bool _installing;

    internal TopAppsSection(PopoverContext context)
    {
        _context = context;

        _placeholder = Ui.Label(PopoverContext.L("Gathering data…"), 12, Ui.Secondary);
        _placeholder.Margin = new Thickness(14, 2, 14, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Top;

        _helperText = Ui.Wrapping(string.Empty, 11);
        _helperButton = Ui.LinkButton(PopoverContext.L("Install helper…"), InstallHelper).Margin(0, 6, 0, 0);
        _needsHelper = Ui.Column(_helperText, _helperButton);
        _needsHelper.Margin = new Thickness(14, 0, 14, 10);

        var body = new Grid { Height = ListHeight, ClipToBounds = true };
        body.Children.Add(_placeholder);
        body.Children.Add(_rows);

        View = Ui.Column(Ui.SectionTitle(PopoverContext.L("Top Apps")), _needsHelper, body);

        _context.Traffic.Sampled += OnSampled;
        _context.Traffic.AvailabilityChanged += (_, _) => UpdateState();
        UpdateState();
    }

    public PopoverSection Kind => PopoverSection.TopApps;

    public FrameworkElement View { get; }

    public void SetActive(bool active)
    {
        if (active)
        {
            _lease ??= _context.Traffic.Acquire();
        }
        else
        {
            _lease?.Dispose();
            _lease = null;
            _aggregator.Reset();
            Show([]);
        }

        UpdateState();
    }

    public void Refresh()
    {
    }

    private void OnSampled(object? sender, TrafficSample sample)
    {
        if (_lease is null)
        {
            return;
        }

        var settings = _context.Settings;
        var hidden = settings.HiddenApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var grace = settings.TopAppsGraceEnabled ? TimeSpan.FromSeconds(settings.TopAppsGraceSeconds) : (TimeSpan?)null;

        var apps = _aggregator.Update(sample.Processes, sample.Elapsed, DateTimeOffset.UtcNow, hidden, grace);
        Show(apps);
    }

    private void UpdateState()
    {
        var availability = _context.Traffic.Availability;
        var blocked = availability is TrafficAvailability.NeedsHelper or TrafficAvailability.HelperOutdated or TrafficAvailability.HelperFailed;

        _needsHelper.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_rows.Parent).Visibility = blocked ? Visibility.Collapsed : Visibility.Visible;

        var helperNewer = _context.Traffic.HelperIsNewer;
        _helperText.Text = availability switch
        {
            TrafficAvailability.HelperOutdated when helperNewer => PopoverContext.L("This NetFluss is older than its helper. Update NetFluss, or reinstall the helper from this version, to see which apps are using the network."),
            TrafficAvailability.HelperOutdated => PopoverContext.L("The NetFluss helper is out of date. Update it to see which apps are using the network."),
            TrafficAvailability.HelperFailed => PopoverContext.L("The NetFluss helper is running, but Windows refused it access to network activity. Reinstalling the helper usually fixes this."),
            _ => PopoverContext.L("Windows only lets administrators watch per-app network traffic. The optional NetFluss helper does it for you; installing it asks for approval once."),
        };

        _helperButton.Content = _installing
            ? PopoverContext.L("Installing…")
            : availability switch
            {
                TrafficAvailability.HelperOutdated when helperNewer => PopoverContext.L("Reinstall helper…"),
                TrafficAvailability.HelperOutdated => PopoverContext.L("Update helper…"),
                TrafficAvailability.HelperFailed => PopoverContext.L("Reinstall helper…"),
                _ => PopoverContext.L("Install helper…"),
            };
        _helperButton.IsEnabled = !_installing;
    }

    private async void InstallHelper()
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        UpdateState();

        var error = await HelperSetup.InstallAsync();
        _installing = false;

        if (error is not null)
        {
            _helperText.Text = error;
        }
        else
        {
            await _context.Helper.ProbeAsync(TimeSpan.FromSeconds(10));
        }

        UpdateState();
    }

    private void Show(IReadOnlyList<AppTraffic> apps)
    {
        _placeholder.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        while (_rowPool.Count < apps.Count)
        {
            var row = new AppRow(this);
            _rowPool.Add(row);
            _rows.Children.Add(row.View);
        }

        var max = apps.Count == 0 ? 1 : Math.Max(apps.Max(a => a.Total), 1);

        for (var i = 0; i < _rowPool.Count; i++)
        {
            if (i < apps.Count)
            {
                _rowPool[i].Update(apps[i], max);
                _rowPool[i].View.Visibility = Visibility.Visible;
            }
            else
            {
                _rowPool[i].View.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Hide(string name)
    {
        _context.Store.Batch(settings => settings.SetAppHidden(name, true));
        Show(_rowPool.Where(r => r.View.Visibility == Visibility.Visible && r.App is not null && r.App.Name != name)
            .Select(r => r.App!)
            .ToList());
    }

    /// <summary>One app row, reused across ticks so the hover state of its hide button holds.</summary>
    private sealed class AppRow
    {
        private readonly TopAppsSection _owner;
        private readonly TextBlock _name;
        private readonly TextBlock _download;
        private readonly TextBlock _upload;
        private readonly Grid _track;
        private readonly Border _fill;
        private readonly Button _hide;
        private double _fraction;

        internal AppRow(TopAppsSection owner)
        {
            _owner = owner;

            _name = Ui.Label(string.Empty, 12, Ui.Text, FontWeights.Medium);
            _download = Ui.Number(string.Empty, 11, Ui.Secondary);
            _upload = Ui.Number(string.Empty, 11, Ui.Secondary);

            var down = Ui.Icon(Glyph.Down, 8, Ui.Download).Margin(0, 0, 3, 0);
            var up = Ui.Icon(Glyph.Up, 8, Ui.Upload).Margin(8, 0, 3, 0);

            _hide = Ui.IconButton(Glyph.EyeHide, PopoverContext.L("Hide from Top Apps"), () =>
            {
                if (App is not null)
                {
                    _owner.Hide(App.Name);
                }
            }, 10);
            _hide.Width = 20;
            _hide.Height = 20;
            _hide.Margin = new Thickness(4, 0, 0, 0);
            _hide.Visibility = Visibility.Hidden;

            var header = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto);
            header.Children.Add(_name.At(0));
            header.Children.Add(Ui.Row(down, _download, up, _upload).At(1));
            header.Children.Add(_hide.At(2));

            (_track, _fill) = Ui.Bar(Ui.Download);
            _track.SizeChanged += (_, _) => Ui.SetFraction(_track, _fill, _fraction);

            var body = Ui.Column(header, _track);
            var frame = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                Background = System.Windows.Media.Brushes.Transparent,
                Child = body,
            };

            // The hide control appears on hover only, as on macOS: always showing it would put
            // a destructive-looking button on every row of a list people mostly just read.
            frame.MouseEnter += (_, _) =>
            {
                _hide.Visibility = Visibility.Visible;
                frame.SetResourceReference(Border.BackgroundProperty, "PopoverHoverBrush");
            };
            frame.MouseLeave += (_, _) =>
            {
                _hide.Visibility = Visibility.Hidden;
                frame.Background = System.Windows.Media.Brushes.Transparent;
            };

            View = frame;
        }

        internal Border View { get; }

        internal AppTraffic? App { get; private set; }

        internal void Update(AppTraffic app, double max)
        {
            App = app;
            var useBits = _owner._context.UseBits;
            _name.Text = app.Name;
            _name.ToolTip = app.Name;
            _download.Text = RateFormatter.FormatRate(app.RxRateBps, useBits);
            _upload.Text = RateFormatter.FormatRate(app.TxRateBps, useBits);
            _fraction = app.Total / max;
            Ui.SetFraction(_track, _fill, _fraction);
        }
    }
}
