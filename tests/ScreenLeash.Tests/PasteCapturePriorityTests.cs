using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 回归：<b>光标在已展开的绳子上时，Ctrl+V 必须被绳子接管，不管前台是什么程序。</b>
///
/// <para>曾经的缺陷：<c>PasteHook.TryPostPasteRequest</c> 先判 <c>FocusProbe.ShouldTakeOverPaste()</c>，
/// 再判快照命中。绳子窗口是 WS_EX_NOACTIVATE，前台始终是被的程序，于是只要前台是浏览器/记事本
/// 这类应用就直接放行，永远走不到命中判定——用户「鼠标放在绳子上按 Ctrl+V 粘不上去」即此。</para>
/// </summary>
public class PasteCapturePriorityTests
{
    [Theory]
    // ① 命中绳子 + 前台可粘贴 → 必须接管（用户报的那个场景）
    [InlineData(EdgeKind.Right, false, true)]
    [InlineData(EdgeKind.Left, false, true)]
    [InlineData(EdgeKind.Top, false, true)]
    [InlineData(EdgeKind.Bottom, false, true)]
    // ② 命中绳子 + 前台无处可粘 → 接管
    [InlineData(EdgeKind.Right, true, true)]
    // ③ 未命中 + 前台无处可粘（桌面/无焦点）→ 接管
    [InlineData(null, true, true)]
    // ④ 未命中 + 前台可粘贴 → 放行，交给系统正常粘贴
    [InlineData(null, false, false)]
    public void ShouldCapture_命中绳子优先于前台判定(EdgeKind? leashHit, bool foregroundHasNoPasteTarget, bool expected)
    {
        Assert.Equal(expected, PasteTargetPolicy.ShouldCapture(leashHit, foregroundHasNoPasteTarget));
    }

    [Fact]
    public void ShouldCapture_命中任意边都成立_与枚举顺序无关()
    {
        foreach (var edge in Enum.GetValues<EdgeKind>())
        {
            Assert.True(
                PasteTargetPolicy.ShouldCapture(edge, foregroundHasNoPasteTarget: false),
                $"命中 {edge} 时必须接管，不该被前台判定拦截");
        }
    }

    /// <summary>
    /// 命中判定本身也要成立：把四条边的展开矩形放进快照，光标落在哪条就返回哪条。
    /// 这里用一个 1920x1080 的工作区，四条展开带各 140px 厚。
    /// </summary>
    [Fact]
    public void HitExpandedLeash_光标在右边展开带内_返回Right()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var targets = new List<LeashTarget>
        {
            new(EdgeKind.Left, new Rectangle(0, 0, 140, 1080)),
            new(EdgeKind.Top, new Rectangle(0, 0, 1920, 140)),
            new(EdgeKind.Right, new Rectangle(1780, 0, 140, 1080)),
            new(EdgeKind.Bottom, new Rectangle(0, 940, 1920, 140)),
        };

        // 右边展开带内（不靠角落）
        Assert.Equal(EdgeKind.Right, PasteTargetPolicy.HitExpandedLeash(targets, new Point(1850, 500)));

        // 配合接管判定：命中 + 前台可粘贴 → 仍然接管
        var hit = PasteTargetPolicy.HitExpandedLeash(targets, new Point(1850, 500));
        Assert.True(PasteTargetPolicy.ShouldCapture(hit, foregroundHasNoPasteTarget: false));
    }

    [Fact]
    public void HitExpandedLeash_光标在屏幕中央_未命中()
    {
        var targets = new List<LeashTarget>
        {
            new(EdgeKind.Right, new Rectangle(1780, 0, 140, 1080)),
        };

        Assert.Null(PasteTargetPolicy.HitExpandedLeash(targets, new Point(960, 540)));
        Assert.False(PasteTargetPolicy.ShouldCapture(null, foregroundHasNoPasteTarget: false));
    }
}
