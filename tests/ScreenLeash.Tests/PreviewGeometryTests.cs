using System;
using System.Drawing;
using ScreenLeash.Core;
using ScreenLeash.UI;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 悬停放大预览的位置计算：纯函数，保证预览永远贴在绳子内侧且不跑出屏幕。
/// </summary>
public class PreviewGeometryTests
{
    private static readonly Rectangle Area = new(0, 0, 1920, 1080);

    [Fact]
    public void Compute_右边绳_预览放在锚点左侧()
    {
        var anchor = new Rectangle(1780, 400, 140, 96);

        var rect = PreviewGeometry.Compute(anchor, new Size(800, 600), Area, 96, EdgeKind.Right);

        Assert.True(rect.Right <= anchor.Left, $"预览右边 {rect.Right} 应落在锚点左边 {anchor.Left} 以内");
        Assert.True(rect.Left >= Area.Left, "预览不该超出工作区左边");
        Assert.True(rect.Top >= Area.Top, "预览不该超出工作区上边");
        Assert.True(rect.Bottom <= Area.Bottom, "预览不该超出工作区下边");
    }

    [Fact]
    public void Compute_左边绳_预览放在锚点右侧()
    {
        var anchor = new Rectangle(0, 400, 140, 96);

        var rect = PreviewGeometry.Compute(anchor, new Size(800, 600), Area, 96, EdgeKind.Left);

        Assert.True(rect.Left >= anchor.Right, $"预览左边 {rect.Left} 应落在锚点右边 {anchor.Right} 之外");
    }

    [Fact]
    public void Compute_高度不超过工作区的六成()
    {
        var anchor = new Rectangle(1780, 400, 140, 96);

        var rect = PreviewGeometry.Compute(anchor, new Size(2000, 4000), Area, 96, EdgeKind.Right);

        var cap = (int)(Area.Height * Metrics.PreviewMaxHeightRatio);
        Assert.True(rect.Height <= cap + 1, $"预览高 {rect.Height} 超过上限 {cap}");
    }

    [Fact]
    public void Compute_小图不放大()
    {
        var anchor = new Rectangle(1780, 400, 140, 96);

        var rect = PreviewGeometry.Compute(anchor, new Size(120, 90), Area, 96, EdgeKind.Right);

        Assert.Equal(120, rect.Width);
        Assert.Equal(90, rect.Height);
    }

    [Fact]
    public void FitInside_保持宽高比()
    {
        var scaled = PreviewGeometry.FitInside(new Size(1600, 900), 1400, 1000);

        Assert.True(scaled.Width > 0 && scaled.Height > 0);
        Assert.Equal(1600.0 / 900.0, (double)scaled.Width / scaled.Height, 2);
    }

    [Fact]
    public void Compute_锚点靠近角落时_仍夹在工作区内()
    {
        var anchor = new Rectangle(1780, 1000, 140, 80);

        var rect = PreviewGeometry.Compute(anchor, new Size(900, 700), Area, 96, EdgeKind.Right);

        Assert.True(rect.Left >= Area.Left);
        Assert.True(rect.Top >= Area.Top);
        Assert.True(rect.Right <= Area.Right);
        Assert.True(rect.Bottom <= Area.Bottom);
    }
}
