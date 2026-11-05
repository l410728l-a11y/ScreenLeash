using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using ScreenLeash.Core;
using ScreenLeash.UI;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>度量/几何/拓扑的事实校验：纯函数必须给出可断言的结果。</summary>
public class MetricsGeometryTests
{
    [Theory]
    [InlineData(6, 96, 6)]
    [InlineData(6, 120, 8)]   // 7.5 → 四舍五入远离零 → 8
    [InlineData(6, 144, 9)]
    [InlineData(140, 96, 140)]
    [InlineData(140, 192, 280)]
    [InlineData(0, 96, 0)]
    public void Px_ScalesByDpi(int dip, int dpi, int expected)
    {
        Assert.Equal(expected, Metrics.Px(dip, dpi));
    }

    [Fact]
    public void Px_RejectsNonPositiveDpi()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.Px(6, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.PxFloat(6f, -1));
    }

    [Fact]
    public void PxFloat_PreservesSubPixelForPenAndFont()
    {
        Assert.Equal(2f, Metrics.PxFloat(Metrics.StrokeWidth, 96));
        Assert.Equal(3f, Metrics.PxFloat(Metrics.StrokeWidth, 144));
        Assert.Equal(8.5f, Metrics.PxFloat(Metrics.FontSize, 96));
    }

    [Fact]
    public void Compute_CollapsedSticksToEdge_ExpandedGrowsInward()
    {
        var area = new Rectangle(0, 0, 1920, 1040);

        var collapsed = EdgeGeometry.Compute(EdgeKind.Right, area, 96, LeashVisualState.Idle);
        Assert.Equal(new Rectangle(1920 - Metrics.CollapsedThickness, 0, Metrics.CollapsedThickness, 1040), collapsed);

        var expanded = EdgeGeometry.Compute(EdgeKind.Right, area, 96, LeashVisualState.Hover);
        Assert.Equal(new Rectangle(1920 - Metrics.ExpandedThickness, 0, Metrics.ExpandedThickness, 1040), expanded);

        var dragging = EdgeGeometry.Compute(EdgeKind.Right, area, 96, LeashVisualState.Dragging);
        Assert.Equal(expanded, dragging);
    }

    [Fact]
    public void Compute_CoversAllFourEdges()
    {
        var area = new Rectangle(100, 50, 800, 600);
        int t = Metrics.CollapsedThickness;

        Assert.Equal(new Rectangle(area.Left, area.Top, t, area.Height), EdgeGeometry.Compute(EdgeKind.Left, area, 96, LeashVisualState.Idle));
        Assert.Equal(new Rectangle(area.Right - t, area.Top, t, area.Height), EdgeGeometry.Compute(EdgeKind.Right, area, 96, LeashVisualState.Idle));
        Assert.Equal(new Rectangle(area.Left, area.Top, area.Width, t), EdgeGeometry.Compute(EdgeKind.Top, area, 96, LeashVisualState.Idle));
        Assert.Equal(new Rectangle(area.Left, area.Bottom - t, area.Width, t), EdgeGeometry.Compute(EdgeKind.Bottom, area, 96, LeashVisualState.Idle));
    }

    [Fact]
    public void Compute_ScalesThicknessWithOwnDpi()
    {
        var area = new Rectangle(0, 0, 3840, 2160);

        var at96 = EdgeGeometry.Compute(EdgeKind.Left, area, 96, LeashVisualState.Hover);
        var at192 = EdgeGeometry.Compute(EdgeKind.Left, area, 192, LeashVisualState.Hover);

        Assert.Equal(Metrics.ExpandedThickness * 2, at192.Width);
        Assert.Equal(Metrics.ExpandedThickness, at96.Width);
    }

    [Fact]
    public void Compute_NeverExceedsWorkingArea()
    {
        var tiny = new Rectangle(0, 0, 40, 30);

        var bottom = EdgeGeometry.Compute(EdgeKind.Bottom, tiny, 192, LeashVisualState.Hover);

        Assert.Equal(tiny.Height, bottom.Height);
        Assert.True(bottom.Height <= tiny.Height);
    }

    [Fact]
    public void TriggerZone_OnlyCoversBandNextToEdge()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        int zone = Metrics.EdgeTriggerZone;

        Assert.True(EdgeGeometry.IsCursorInTriggerZone(EdgeKind.Right, area, 96, new Point(1919, 500)));
        Assert.True(EdgeGeometry.IsCursorInTriggerZone(EdgeKind.Right, area, 96, new Point(1920 - zone + 1, 500)));
        Assert.False(EdgeGeometry.IsCursorInTriggerZone(EdgeKind.Right, area, 96, new Point(1920 - zone - 1, 500)));
        Assert.False(EdgeGeometry.IsCursorInTriggerZone(EdgeKind.Right, area, 96, new Point(1919, 2000)));
        Assert.False(EdgeGeometry.IsCursorInTriggerZone(EdgeKind.Left, area, 96, new Point(1919, 500)));
    }

    [Fact]
    public void HitEdge_PicksNearestOnCornerOverlap()
    {
        var area = new Rectangle(0, 0, 1920, 1080);

        Assert.Equal(EdgeKind.Left, EdgeGeometry.HitEdge(area, 96, new Point(1, 1)));
        Assert.Equal(EdgeKind.Right, EdgeGeometry.HitEdge(area, 96, new Point(1918, 500)));
        Assert.Null(EdgeGeometry.HitEdge(area, 96, new Point(960, 540)));
    }

    [Fact]
    public void BuildSlots_ProducesFourEdgesPerScreen()
    {
        var screens = new[]
        {
            new ScreenInfo("DISPLAY1", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96, true),
            new ScreenInfo("DISPLAY2", new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144, false),
        };

        var all = ScreenTopology.BuildSlots(screens, onlyPrimaryScreen: false);
        Assert.Equal(8, all.Count);
        Assert.Equal(4, all.Count(s => s.Screen.DeviceName == "DISPLAY1"));
        Assert.Equal(4, all.Count(s => s.Screen.DeviceName == "DISPLAY2"));
        Assert.Equal(Enum.GetValues<EdgeKind>().Length, all.Select(s => s.Edge).Distinct().Count());

        var primaryOnly = ScreenTopology.BuildSlots(screens, onlyPrimaryScreen: true);
        Assert.Equal(4, primaryOnly.Count);
        Assert.All(primaryOnly, s => Assert.True(s.Screen.Primary));
    }

    [Fact]
    public void BuildSlots_UsesEachScreensOwnDpi()
    {
        var screens = new[]
        {
            new ScreenInfo("A", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96, true),
            new ScreenInfo("B", new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144, false),
        };

        var slots = ScreenTopology.BuildSlots(screens, onlyPrimaryScreen: false);
        var bLeft = slots.Single(s => s.Screen.DeviceName == "B" && s.Edge == EdgeKind.Left);
        var aLeft = slots.Single(s => s.Screen.DeviceName == "A" && s.Edge == EdgeKind.Left);

        Assert.Equal(new Rectangle(1920, 0, Metrics.Px(Metrics.CollapsedThickness, 144), 1400), bLeft.Bounds(LeashVisualState.Idle));
        Assert.Equal(new Rectangle(0, 0, Metrics.CollapsedThickness, 1040), aLeft.Bounds(LeashVisualState.Idle));
        Assert.Equal(Metrics.Px(Metrics.ExpandedThickness, 144), bLeft.Bounds(LeashVisualState.Hover).Width);
    }

    [Fact]
    public void BuildSlots_FallsBackWhenPrimaryFlagMissing()
    {
        var screens = new[]
        {
            new ScreenInfo("A", new Rectangle(0, 0, 800, 600), new Rectangle(0, 0, 800, 560), 96, false),
        };

        var slots = ScreenTopology.BuildSlots(screens, onlyPrimaryScreen: true);

        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.Equal("A", s.Screen.DeviceName));
    }

    [Fact]
    public void ScreenFromPoint_ResolvesContainingScreen()
    {
        var screens = new[]
        {
            new ScreenInfo("A", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96, true),
            new ScreenInfo("B", new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144, false),
        };

        Assert.Equal("A", ScreenTopology.ScreenFromPoint(screens, new Point(10, 10))!.Value.DeviceName);
        Assert.Equal("B", ScreenTopology.ScreenFromPoint(screens, new Point(2000, 10))!.Value.DeviceName);
        Assert.Null(ScreenTopology.ScreenFromPoint(screens, new Point(-5, 10)));
    }

    [Fact]
    public void Topology_RebuildsOnDemand_AndRaisesEvent()
    {
        var current = new List<ScreenInfo>
        {
            new("A", new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 96, true),
        };

        using var topology = new ScreenTopology(onlyPrimaryScreen: false, probe: () => current.ToArray(), sync: null);
        int rebuilt = 0;
        topology.Rebuilt += (_, _) => rebuilt++;

        Assert.Equal(4, topology.Slots.Count);

        current.Add(new ScreenInfo("B", new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), 144, false));
        topology.Rebuild();

        Assert.Equal(8, topology.Slots.Count);
        Assert.Equal(1, rebuilt);
    }
}
