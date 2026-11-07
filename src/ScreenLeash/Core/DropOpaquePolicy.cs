using System;

namespace ScreenLeash.Core;

/// <summary>
/// 收起态「临时取消透明键」的判据。
///
/// <para><b>为什么需要取消透明</b>：<c>TransparencyKey</c> 抠掉的像素连 OLE 拖放命中也会一起穿透，
/// 所以收起态（窗口只有 <see cref="Metrics.CollapsedThickness"/> 宽，除了一条 2.5dip 的绳线全是透明像素）
/// 如果不临时取消透明，拖东西落上去根本收不到 <c>OnDragEnter</c>。</para>
///
/// <para><b>为什么不能只看左键是否按下</b>：那样在桌面或任何应用里点一下（哪怕不移动）都会让
/// 右边那条 40dip 的窗口整块变成实色近黑——表现为「每次点击右边都闪一个黑框」。
/// 用户实测反馈过两次（早期「单击图标就整块变黑」和现在的「点桌面任何地方都闪」）。</para>
///
/// <para><b>真正的判据</b>：只有同时满足三条才值得变成不透明——</para>
/// <list type="number">
/// <item>左键按着（松手立刻恢复）；</item>
/// <item>按下后光标确实移动过（拖放的特征；单纯点击/双击不移动）；</item>
/// <item>光标落在该绳子窗口矩形内（拖东西已经靠近到会命中的位置）。</item>
/// </list>
/// <para>外部拖入已经进入窗口（<paramref name="dropHighlighted"/>）时无条件保持不透明——
/// 那是 449c0b0 的修复：展开后恢复透明会让 OLE 判定光标离开 → OnDragLeave → 收起 → 再 DragEnter，
/// 表现为「可拖入区间闪一下就缩回去」。</para>
/// </summary>
public static class DropOpaquePolicy
{
    /// <summary>
    /// 当前这一帧是否应该把窗口切成不透明（仅收起态需要；展开态只由外部拖入触发）。
    /// </summary>
    /// <param name="leftDown">左键当前是否按下。</param>
    /// <param name="movedWhilePressed">本次按下后光标是否移动超过系统拖拽阈值。</param>
    /// <param name="dropHighlighted">外部东西正拖在本窗口上（已收到 DragEnter）。</param>
    /// <param name="expanded">窗口当前是否展开。</param>
    /// <param name="cursorInsideWindow">光标是否落在本窗口矩形内（屏幕坐标）。</param>
    public static bool ShouldBeOpaque(
        bool leftDown,
        bool movedWhilePressed,
        bool dropHighlighted,
        bool expanded,
        bool cursorInsideWindow)
    {
        if (!leftDown)
        {
            return false;
        }

        // 已进入拖放流程：全程不透明，直到 DragLeave / DragDrop 自己收尾。
        if (dropHighlighted)
        {
            return true;
        }

        // 展开态本来就有大片不透明像素，再取消透明只会让整块面板变近黑。
        if (expanded)
        {
            return false;
        }

        // 收起态：必须是「按着并移动」且光标已经落在窗口里，才算可能在拖东西进来。
        return movedWhilePressed && cursorInsideWindow;
    }
}
