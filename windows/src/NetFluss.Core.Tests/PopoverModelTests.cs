// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

/// <summary>Section ordering and the per-section toggles that alias existing preferences.</summary>
public class PopoverSectionTests
{
    [Fact]
    public void EmptyOrder_IsTheMacDefault()
        => Assert.Equal(PopoverSections.DefaultOrder, PopoverSections.Resolve([]));

    [Fact]
    public void StoredOrder_IsKept_AndNewSectionsAreAppended()
    {
        // A settings file written before "usage" and "vpn" existed.
        var resolved = PopoverSections.Resolve(["wifi", "totals", "adapters"]);

        Assert.Equal(PopoverSection.Wifi, resolved[0]);
        Assert.Equal(PopoverSection.Totals, resolved[1]);
        Assert.Equal(PopoverSection.Adapters, resolved[2]);
        Assert.Equal(PopoverSections.DefaultOrder.Count, resolved.Count);
        Assert.Contains(PopoverSection.Usage, resolved);
    }

    [Fact]
    public void UnknownAndDuplicateIds_AreDropped()
    {
        var resolved = PopoverSections.Resolve(["dns", "bogus", "dns", "topApps"]);

        Assert.Equal(PopoverSection.Dns, resolved[0]);
        Assert.Equal(PopoverSection.TopApps, resolved[1]);
        Assert.Equal(resolved.Count, resolved.Distinct().Count());
    }

    [Fact]
    public void Ids_AreTheMacRawValues()
    {
        Assert.Equal("topApps", PopoverSection.TopApps.Id());
        Assert.Equal(PopoverSection.TopApps, PopoverSections.FromId("topApps"));
        Assert.Null(PopoverSections.FromId("TopApps"));
    }

    [Fact]
    public void ConnectionToggle_RemembersTheViewItWasIn()
    {
        var settings = new AppSettings { ConnectionMode = ConnectionDisplayMode.List };

        settings.SetSectionEnabled(PopoverSection.Connection, false);
        Assert.Equal(ConnectionDisplayMode.None, settings.ConnectionMode);
        Assert.False(settings.IsSectionEnabled(PopoverSection.Connection));

        settings.SetSectionEnabled(PopoverSection.Connection, true);
        Assert.Equal(ConnectionDisplayMode.List, settings.ConnectionMode);
    }

    [Fact]
    public void UsageToggle_SwitchesStatisticsCollectionOn()
    {
        var settings = new AppSettings();
        settings.SetSectionEnabled(PopoverSection.Usage, true);

        Assert.True(settings.ShowUsageSummary);
        Assert.True(settings.CollectStatistics);
    }

    [Fact]
    public void SectionToggles_AliasTheExistingPreferences()
    {
        var settings = new AppSettings();

        settings.SetSectionEnabled(PopoverSection.Dns, true);
        settings.SetSectionEnabled(PopoverSection.Wifi, true);
        settings.SetSectionEnabled(PopoverSection.TopApps, true);

        Assert.True(settings.ShowDnsSwitcher);
        Assert.True(settings.ShowWifiSwitcher);
        Assert.True(settings.ShowTopApps);
    }

    [Fact]
    public void RouterSection_FollowsTheRouterIntegrations()
    {
        var settings = new AppSettings();
        Assert.False(settings.IsSectionEnabled(PopoverSection.Router));

        settings.FritzBoxEnabled = true;
        Assert.True(settings.IsSectionEnabled(PopoverSection.Router));
    }

    [Fact]
    public void SectionOrder_ChangeIsPersistedAndNotifies()
    {
        var settings = new AppSettings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.SetSectionOrder([PopoverSection.Wifi, PopoverSection.Totals]);

        Assert.Contains(nameof(AppSettings.PopoverSectionOrder), raised);
        Assert.Equal(PopoverSection.Wifi, settings.SectionOrder()[0]);
    }
}

/// <summary>DNS preset ordering and hiding in the popover switcher.</summary>
public class DnsPresetOrderTests
{
    [Fact]
    public void HiddenPresets_StayInPreferences_ButLeaveThePopover()
    {
        var settings = new AppSettings();
        settings.SetDnsPresetHidden("google", true);

        Assert.Contains(settings.OrderedDnsPresets(), p => p.Id == "google");
        Assert.DoesNotContain(settings.VisibleDnsPresets(), p => p.Id == "google");
    }

    [Fact]
    public void MovingAPreset_ReordersTheList()
    {
        var settings = new AppSettings();
        settings.MoveDnsPreset("quad9", 0);

        Assert.Equal("quad9", settings.OrderedDnsPresets()[0].Id);
        Assert.Equal("system", settings.OrderedDnsPresets()[1].Id);
    }
}
