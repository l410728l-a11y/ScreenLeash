using System;
using System.Drawing;

namespace ScreenLeash.Core;

/// <summary>屏幕的四条边，绳子逐边各有一条。</summary>
public enum EdgeKind
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>绳子的视觉状态；只有 <see cref="Idle"/> 是收起态。</summary>
public enum LeashVisualState
{
    Idle,
    Hover,
    Dragging,
}

/// <summary>
/// 窗口边界的唯一出口：纯函数、无 IO、无 WinForms 依赖。
/// 任何窗口矩形都必须由 <see cref="Compute"/> 得出，调用方不得自行加减边缘偏移。
/// </summary>
public static class EdgeGeometry
{
    /// <summary>该视觉状态是否展开（Hover / Dragging 均展开）。</summary>
    public static bool IsExpanded(LeashVisualState state) => state != LeashVisualState.Idle;

    /// <summary>
    /// 计算某条边在某屏幕工作区上的窗口矩形。
    /// 收起态为贴边的 <see cref="Metrics.CollapsedThickness"/> 细线，展开态
    /// 为向屏内伸出 <see cref="Metrics.ExpandedThickness"/> 的面板；沿边方向始终贯穿工作区。
    /// </summary>
    /// <param name="edge">目标边。</param>
    /// <param name="workingArea">该屏幕的工作区（已排除任务栏）。</param>
    /// <param name="dpi">该屏幕的 DPI（每屏用自己的 DeviceDpi）。</param>
    /// <param name="state">视觉状态。</param>
    public static Rectangle Compute(EdgeKind edge, Rectangle workingArea, int dpi, LeashVisualState state)
    {
        int thicknessDip = IsExpanded(state) ? Metrics.ExpandedThickness : Metrics.CollapsedThickness;
        int thickness = Metrics.Px(thicknessDip, dpi);
        bool vertical = edge is EdgeKind.Left or EdgeKind.Right;
        // 防御：工作区比面板还窄时不让矩形溢出屏幕。
        int extent = vertical ? workingArea.Width : workingArea.Height;
        if (thickness > extent)
        {
            thickness = extent;
        }

        return edge switch
        {
            EdgeKind.Left => new Rectangle(workingArea.Left, workingArea.Top, thickness, workingArea.Height),
            EdgeKind.Right => new Rectangle(workingArea.Right - thickness, workingArea.Top, thickness, workingArea.Height),
            EdgeKind.Top => new Rectangle(workingArea.Left, workingArea.Top, workingArea.Width, thickness),
            EdgeKind.Bottom => new Rectangle(workingArea.Left, workingArea.Bottom - thickness, workingArea.Width, thickness),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "未知的边。"),
        };
    }

    /// <summary>
    /// 按**给定厚度**（px）计算窗口矩形，用于收起↔展开之间的动画中间态。
    /// 厚度会被夹到 [0, 工作区沿垂直于边方向的长度]，避免动画中间帧溢出屏幕。
    /// </summary>
    /// <param name="edge">目标边。</param>
    /// <param name="workingArea">该屏幕工作区。</param>
    /// <param name="thicknessPx">当前厚度（px），由缓动计算得出。</param>
    public static Rectangle ComputeWithThickness(EdgeKind edge, Rectangle workingArea, int thicknessPx)
    {
        bool vertical = edge is EdgeKind.Left or EdgeKind.Right;
        int extent = vertical ? workingArea.Width : workingArea.Height;
        int thickness = Math.Clamp(thicknessPx, 0, extent);

        return edge switch
        {
            EdgeKind.Left => new Rectangle(workingArea.Left, workingArea.Top, thickness, workingArea.Height),
            EdgeKind.Right => new Rectangle(workingArea.Right - thickness, workingArea.Top, thickness, workingArea.Height),
            EdgeKind.Top => new Rectangle(workingArea.Left, workingArea.Top, workingArea.Width, thickness),
            EdgeKind.Bottom => new Rectangle(workingArea.Left, workingArea.Bottom - thickness, workingArea.Width, thickness),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "未知的边。"),
        };
    }

    /// <summary>
    /// 光标是否落在该边的触发带内（距边 <see cref="Metrics.EdgeTriggerZone"/> DIP），
    /// 用于「拖近/靠近即弹出」判定。触发带以工作区为界裁剪。
    /// </summary>
    public static bool IsCursorInTriggerZone(EdgeKind edge, Rectangle workingArea, int dpi, Point cursor)
    {
        int zone = Metrics.Px(Metrics.EdgeTriggerZone, dpi);
        bool insideAlong = edge switch
        {
            EdgeKind.Left or EdgeKind.Right => cursor.Y >= workingArea.Top && cursor.Y < workingArea.Bottom,
            EdgeKind.Top or EdgeKind.Bottom => cursor.X >= workingArea.Left && cursor.X < workingArea.Right,
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "未知的边。"),
        };

        if (!insideAlong)
        {
            return false;
        }

        return edge switch
        {
            EdgeKind.Left => cursor.X >= workingArea.Left && cursor.X < workingArea.Left + zone,
            EdgeKind.Right => cursor.X <= workingArea.Right - 1 && cursor.X > workingArea.Right - 1 - zone,
            EdgeKind.Top => cursor.Y >= workingArea.Top && cursor.Y < workingArea.Top + zone,
            EdgeKind.Bottom => cursor.Y <= workingArea.Bottom - 1 && cursor.Y > workingArea.Bottom - 1 - zone,
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "未知的边。"),
        };
    }

    /// <summary>
    /// 光标命中的边（按触发带判定）；不在任何带内返回 <c>null</c>。
    /// 四条带在角上会重叠，此时返回距离最近的那条。
    /// </summary>
    public static EdgeKind? HitEdge(Rectangle workingArea, int dpi, Point cursor)
    {
        EdgeKind? best = null;
        int bestDistance = int.MaxValue;
        foreach (EdgeKind edge in Enum.GetValues<EdgeKind>())
        {
            if (!IsCursorInTriggerZone(edge, workingArea, dpi, cursor))
            {
                continue;
            }

            int distance = edge switch
            {
                EdgeKind.Left => cursor.X - workingArea.Left,
                EdgeKind.Right => workingArea.Right - 1 - cursor.X,
                EdgeKind.Top => cursor.Y - workingArea.Top,
                _ => workingArea.Bottom - 1 - cursor.Y,
            };

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = edge;
            }
        }

        return best;
    }
}
