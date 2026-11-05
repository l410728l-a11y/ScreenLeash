using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenLeash.Core;
using ScreenLeash.Interop;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 全局粘贴**接线**层的断言：消息协议往返、快照发布/读取、钩子判定的纯组合逻辑。
/// 这里不碰窗口与钩子（那需要真实桌面会话），只锁「能被单测固定下来的契约」：
/// 一旦有一侧改了编码偏移或命中语义，这些用例必须先红。
/// </summary>
public class PasteWiringTests
{
    [Theory]
    [InlineData(EdgeKind.Left)]
    [InlineData(EdgeKind.Top)]
    [InlineData(EdgeKind.Right)]
    [InlineData(EdgeKind.Bottom)]
    public void PasteRequestCodec_RoundTripsEveryEdge(EdgeKind edge)
    {
        IntPtr encoded = PasteRequestCodec.Encode(edge);

        // 0 是「未命中」哨兵，合法边绝不能编成 0（EdgeKind.Left 的枚举值就是 0）。
        Assert.NotEqual(IntPtr.Zero, encoded);
        Assert.Equal(edge, PasteRequestCodec.Decode(encoded));
    }

    [Fact]
    public void PasteRequestCodec_ZeroMeansFallbackToActiveEdge()
    {
        Assert.Equal(IntPtr.Zero, PasteRequestCodec.Encode(null));
        Assert.Null(PasteRequestCodec.Decode(IntPtr.Zero));
    }

    [Theory]
    [InlineData(5)]      // 合法边之后的一个值
    [InlineData(99)]
    [InlineData(-1)]
    public void PasteRequestCodec_RejectsOutOfRange(long raw)
    {
        Assert.Null(PasteRequestCodec.Decode(new IntPtr(raw)));
    }

    [Theory]
    [InlineData(true, false, false, true)]   // 裸 Ctrl+V → 键位匹配
    [InlineData(false, false, false, false)] // 没按 Ctrl → 不匹配
    [InlineData(true, true, false, false)]   // Ctrl+Alt+V（本程序降级热键）→ 不匹配
    [InlineData(true, false, true, false)]   // Ctrl+Shift+V（粘贴为纯文本）→ 不匹配
    public void PasteHook_IsPasteChord_AcceptsOnlyBareCtrlV(
        bool ctrlDown,
        bool altDown,
        bool shiftDown,
        bool expected)
    {
        bool chord = PasteHook.IsPasteChord(
            InteropConstants.VkKeyV,
            ctrlDown,
            altDown,
            shiftDown);

        Assert.Equal(expected, chord);
    }

    [Fact]
    public void PasteHook_IsPasteChord_IgnoresOtherKeys()
    {
        bool chord = PasteHook.IsPasteChord(
            InteropConstants.VkKeyV + 1,
            ctrlDown: true,
            altDown: false,
            shiftDown: false);

        Assert.False(chord);
    }

    [Theory]
    [InlineData(true, true)]    // 无粘贴目标 → 接管
    [InlineData(false, false)]  // 有粘贴目标 → 放行
    public void PasteTargetPolicy_ShouldTakeOverCombinesForceAndForeground(
        bool foregroundHasNoPasteTarget,
        bool expected)
    {
        // 钩子回调的组合判定 = PasteHook.IsPasteChord ∧ PasteTargetPolicy.ShouldTakeOver(...)。
        Assert.Equal(expected, PasteTargetPolicy.ShouldTakeOver(force: false, foregroundHasNoPasteTarget));
    }

    [Fact]
    public void LeashSnapshot_HitTestReturnsTheOwningEdge()
    {
        var snapshot = new LeashSnapshot(new[]
        {
            new LeashTarget(EdgeKind.Right, new Rectangle(1000, 0, 140, 900)),
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 2000, 140)),
        });

        Assert.Equal(EdgeKind.Right, snapshot.HitTest(new Point(1060, 500)));
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public void LeashSnapshot_HitTestMissIsNullForFallback()
    {
        var snapshot = new LeashSnapshot(new[]
        {
            new LeashTarget(EdgeKind.Left, new Rectangle(0, 0, 140, 900)),
        });

        Assert.Null(snapshot.HitTest(new Point(1200, 500)));
    }

    [Fact]
    public void LeashSnapshotSlot_PublishesAndReadsWholeSnapshots()
    {
        var slot = new LeashSnapshotSlot();
        var published = new LeashSnapshot(new[]
        {
            new LeashTarget(EdgeKind.Bottom, new Rectangle(0, 700, 2000, 140)),
        });

        slot.Publish(published);

        // 引用赋值 + Volatile 栅栏：读到的要么是旧空快照、要么是刚发布的这一份，不会混。
        Assert.Same(published, slot.Current);
        Assert.Equal(EdgeKind.Bottom, slot.Current.HitTest(new Point(500, 760)));

        slot.Publish(null);
        Assert.Same(LeashSnapshot.Empty, slot.Current);
    }

    [Fact]
    public void LeashSnapshot_IsImmutableAgainstCallerMutation()
    {
        var targets = new List<LeashTarget>
        {
            new(EdgeKind.Left, new Rectangle(0, 0, 140, 900)),
        };
        var snapshot = new LeashSnapshot(targets.ToArray());

        targets.Clear();

        Assert.Single(snapshot.Targets);
        Assert.Equal(EdgeKind.Left, snapshot.HitTest(new Point(10, 400)));
    }
}
