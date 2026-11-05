using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 粘贴命中策略与 wParam 编解码的纯函数测试。
/// 规则来源（plan/paste-policy 本轮修订）：
///   命中 = 收集所有 Bounds.Contains(cursor) 的 target；0 个 → null；1 个 → 该边；
///   多个（角上重叠）→ 取**矩形中心到光标欧氏距离**最小者；距离完全相等 → 取 EdgeKind 枚举序更小者（稳定，不随遍历顺序抖动）。
/// </summary>
public class PasteTargetPolicyTests
{
    // ─── ① 四条边各自命中 ───────────────────────────────────────────
    // 单个 target 包含光标时必须原样返回它自己的边——这是「挂到光标所在边」的最小保证。
    [Theory]
    [InlineData(EdgeKind.Left)]
    [InlineData(EdgeKind.Top)]
    [InlineData(EdgeKind.Right)]
    [InlineData(EdgeKind.Bottom)]
    public void Hit_四边各自命中_返回对应边(EdgeKind edge)
    {
        var bounds = new Rectangle(100, 200, 40, 300);
        var targets = new[] { new LeashTarget(edge, bounds) };

        // 取矩形内一点（右下内缩 1 像素，避开 Rectangle.Contains 的右下开区间）。
        var cursor = new Point(bounds.Left + 5, bounds.Top + 5);

        Assert.Equal(edge, PasteTargetPolicy.HitExpandedLeash(targets, cursor));
    }

    // ─── ② 所有矩形外 → null ────────────────────────────────────────
    [Fact]
    public void Hit_光标在所有矩形外_返回null()
    {
        var targets = new[]
        {
            new LeashTarget(EdgeKind.Left, new Rectangle(0, 0, 40, 1000)),
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 1920, 40)),
        };

        Assert.Null(PasteTargetPolicy.HitExpandedLeash(targets, new Point(900, 600)));
    }

    // ─── ③ 空 targets → null ──────────────────────────────────────
    [Fact]
    public void Hit_空targets_返回null()
    {
        Assert.Null(PasteTargetPolicy.HitExpandedLeash(Array.Empty<LeashTarget>(), new Point(0, 0)));
        Assert.Null(PasteTargetPolicy.HitExpandedLeash(new List<LeashTarget>(), new Point(5, 5)));
    }

    // ─── ④ 角落重叠：按「光标到该边边界」的距离消歧 ────────────────
    // 反例来源（council wenqu）：用「矩形中心距离」会在宽屏下恒判给 Left/Right——
    // Top 矩形中心在 (960,70)，Left 矩形中心在 (70,540)；光标 (40,2) 到 Top 中心约 1845px、
    // 到 Left 中心约 1077px，会被判成 Left，但它明明贴着上边。
    [Fact]
    public void Hit_左上角_光标贴顶_返回Top()
    {
        var targets = new[]
        {
            new LeashTarget(EdgeKind.Left, new Rectangle(0, 0, 140, 1080)),
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 1920, 140)),
        };
        var cursor = new Point(40, 2); // 距上边 2px、距左边 40px

        Assert.True(targets[0].Bounds.Contains(cursor));
        Assert.True(targets[1].Bounds.Contains(cursor));

        Assert.Equal(EdgeKind.Top, PasteTargetPolicy.HitExpandedLeash(targets, cursor));
    }

    // 对称方向：光标距左边 2px、距上边 40px → 必须判 Left。
    [Fact]
    public void Hit_左上角_光标贴左_返回Left()
    {
        var targets = new[]
        {
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 1920, 140)),
            new LeashTarget(EdgeKind.Left, new Rectangle(0, 0, 140, 1080)),
        };
        var cursor = new Point(2, 40);

        Assert.True(targets[0].Bounds.Contains(cursor));
        Assert.True(targets[1].Bounds.Contains(cursor));

        Assert.Equal(EdgeKind.Left, PasteTargetPolicy.HitExpandedLeash(targets, cursor));
    }

    // ─── ⑤ 距离完全相等 → EdgeKind 枚举序更小者（稳定，不随遍历顺序抖动）──
    // 光标 (5,5) 到上边与左边都是 5px，此时只能靠枚举序定胜负（Left=0 < Top=1）。
    [Fact]
    public void Hit_等距时_返回枚举序更小者_与遍历顺序无关()
    {
        var rect = new Rectangle(0, 0, 140, 140);
        var cursor = new Point(5, 5);

        var leftFirst = new[]
        {
            new LeashTarget(EdgeKind.Left, rect),
            new LeashTarget(EdgeKind.Top, rect),
        };
        var topFirst = new[]
        {
            new LeashTarget(EdgeKind.Top, rect),
            new LeashTarget(EdgeKind.Left, rect),
        };

        Assert.Equal(EdgeKind.Left, PasteTargetPolicy.HitExpandedLeash(leftFirst, cursor));
        Assert.Equal(EdgeKind.Left, PasteTargetPolicy.HitExpandedLeash(topFirst, cursor));

        // 非并列的对称验证：光标 (70,135) 到上边 135px、到下边 5px → Bottom。
        var topVsBottom = new[]
        {
            new LeashTarget(EdgeKind.Top, rect),
            new LeashTarget(EdgeKind.Bottom, rect),
        };
        Assert.Equal(EdgeKind.Bottom, PasteTargetPolicy.HitExpandedLeash(topVsBottom, new Point(70, 135)));
    }

    // 稳定性补充：同一份输入连续多次求值结果不变（不依赖任何隐藏状态）。
    [Fact]
    public void Hit_同一输入重复求值_结果稳定()
    {
        var targets = new[]
        {
            new LeashTarget(EdgeKind.Bottom, new Rectangle(0, 0, 100, 100)),
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 100, 100)),
            new LeashTarget(EdgeKind.Right, new Rectangle(0, 0, 100, 100)),
        };
        var cursor = new Point(50, 50);

        var first = PasteTargetPolicy.HitExpandedLeash(targets, cursor);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(first, PasteTargetPolicy.HitExpandedLeash(targets, cursor));
        }
    }

    // 大坐标不溢出：距离用平移到中心的差值平方，2 × 光标 - (left+right) 在 int 范围内会溢出。
    [Fact]
    public void Hit_超大坐标_不溢出且能判定()
    {
        var wide = new Rectangle(0, 0, 20000, 12000);
        var narrow = new Rectangle(0, 0, 400, 12000);
        var cursor = new Point(10, 10);

        var targets = new[]
        {
            new LeashTarget(EdgeKind.Top, wide),
            new LeashTarget(EdgeKind.Left, narrow),
        };

        Assert.Equal(EdgeKind.Left, PasteTargetPolicy.HitExpandedLeash(targets, cursor));
    }

    // ─── ⑥ ShouldTakeOver 四组合 ───────────────────────────────────
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)] // force=true 且前台**可**粘贴 → 仍接管（用户显式强制优先）
    public void ShouldTakeOver_四组合(bool force, bool foregroundHasNoPasteTarget, bool expected)
    {
        Assert.Equal(expected, PasteTargetPolicy.ShouldTakeOver(force, foregroundHasNoPasteTarget));
    }

    // ─── ⑦ wParam 编解码 ──────────────────────────────────────────
    [Theory]
    [InlineData(EdgeKind.Left)]
    [InlineData(EdgeKind.Top)]
    [InlineData(EdgeKind.Right)]
    [InlineData(EdgeKind.Bottom)]
    public void Codec_四边往返(EdgeKind edge)
    {
        var encoded = PasteRequestCodec.Encode(edge);

        Assert.NotEqual(IntPtr.Zero, encoded); // 任何合法边都不得与「未命中」哨兵撞车
        Assert.Equal(edge, PasteRequestCodec.Decode(encoded));
    }

    [Fact]
    public void Codec_Encode_null_等于Zero()
    {
        Assert.Equal(IntPtr.Zero, PasteRequestCodec.Encode(null));
        Assert.Null(PasteRequestCodec.Decode(IntPtr.Zero));
    }

    [Fact]
    public void Codec_非法值一律null_不抛不返回非法枚举()
    {
        Assert.Null(PasteRequestCodec.Decode(new IntPtr(99)));  // 越界
        Assert.Null(PasteRequestCodec.Decode(new IntPtr(-1)));  // 负数
        Assert.Null(PasteRequestCodec.Decode(new IntPtr(-99))); // 负越界
        Assert.Null(PasteRequestCodec.Decode(new IntPtr(5)));   // 合法范围内但未定义（枚举只有 4 个成员）
    }

    [Fact]
    public void Codec_编码值是枚举值加一_与哨兵零无冲突()
    {
        Assert.Equal(new IntPtr(1), PasteRequestCodec.Encode(EdgeKind.Left));
        Assert.Equal(new IntPtr(2), PasteRequestCodec.Encode(EdgeKind.Top));
        Assert.Equal(new IntPtr(3), PasteRequestCodec.Encode(EdgeKind.Right));
        Assert.Equal(new IntPtr(4), PasteRequestCodec.Encode(EdgeKind.Bottom));
    }

    // ─── 命中 + 编解码端到端（钩子线程 post → UI 线程 decode 的真实路径）──
    [Fact]
    public void 命中边经编解码往返_仍落到同一条边()
    {
        var targets = new[] { new LeashTarget(EdgeKind.Bottom, new Rectangle(0, 1100, 1920, 52)) };
        var cursor = new Point(960, 1120);

        var hit = PasteTargetPolicy.HitExpandedLeash(targets, cursor);
        var decoded = PasteRequestCodec.Decode(PasteRequestCodec.Encode(hit));

        Assert.Equal(EdgeKind.Bottom, decoded);
    }
}
