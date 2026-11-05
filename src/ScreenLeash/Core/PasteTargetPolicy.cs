using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace ScreenLeash.Core;

/// <summary>
/// 一个「展开态绳子」的命中矩形。带上边号是为了让命中**直接给出目标边**，
/// 而不是回头去看 <c>_activeEdge</c> 这类 tick 粒度（120ms）才更新的状态。
/// </summary>
/// <param name="Edge">该矩形属于哪条边。</param>
/// <param name="Bounds">该边展开态在屏幕坐标系下的窗口矩形（px）。</param>
public readonly record struct LeashTarget(EdgeKind Edge, Rectangle Bounds);

/// <summary>
/// 一份不可变快照：钩子线程（后台 STA）读、UI 线程写，双方不得共享可变状态。
/// 不可变的理由不是洁癖——如果钩子线程直接读 UI 层的可变集合，就可能读到
/// 「左边是旧矩形、右边是新矩形」的混合值，命中判定会落到一条不该命中的边。
/// </summary>
public sealed class LeashSnapshot
{
    /// <summary>空快照：卸钩、四边全关、无展开边时的唯一合法表示。</summary>
    public static readonly LeashSnapshot Empty = new(Array.Empty<LeashTarget>());

    private readonly LeashTarget[] _targets;

    /// <param name="targets">展开态目标；传 null 视作空（防御，不抛）。</param>
    public LeashSnapshot(LeashTarget[] targets)
    {
        _targets = targets ?? Array.Empty<LeashTarget>();
    }

    /// <summary>只读目标集合；数组实例本身由本类独占，调用方不得就地修改。</summary>
    public IReadOnlyList<LeashTarget> Targets => _targets;

    /// <summary>目标数量。</summary>
    public int Count => _targets.Length;

    /// <summary>按本快照做命中判定（语义与 <see cref="PasteTargetPolicy.HitExpandedLeash"/> 完全一致）。</summary>
    public EdgeKind? HitTest(Point cursor) => PasteTargetPolicy.HitExpandedLeash(_targets, cursor);
}

/// <summary>
/// 快照的发布槽：唯一的跨线程交接点。引用赋值在 .NET 的内存模型下是原子的，
/// 配合 <see cref="Volatile"/> 的写-读栅栏，读方要么看到旧快照、要么看到新快照，看不到半个。
/// </summary>
public sealed class LeashSnapshotSlot
{
    private LeashSnapshot _current = LeashSnapshot.Empty;

    /// <summary>当前快照（永不为 null）。</summary>
    public LeashSnapshot Current => Volatile.Read(ref _current);

    /// <summary>发布新快照；传 null 等价于发布 <see cref="LeashSnapshot.Empty"/>。</summary>
    public void Publish(LeashSnapshot? snapshot) => Volatile.Write(ref _current, snapshot ?? LeashSnapshot.Empty);
}

/// <summary>
/// <c>WM_APP_PASTE</c> 的 wParam 编解码协议。**不用强转 (IntPtr)(int)edge**：
/// <see cref="EdgeKind.Left"/> 是 0，与「未命中」哨兵 <see cref="IntPtr.Zero"/> 撞车。
/// 协议：wParam = 枚举值 + 1（Left→1 … Bottom→4），0 保留给「未命中，回退 _activeEdge」。
/// 解码对任何非法值只返回 null，不抛异常、不返回未定义的枚举成员——钩子回调与窗口消息
/// 的输入来自进程外，不能假设它守规矩。
/// </summary>
public static class PasteRequestCodec
{
    /// <summary>编码为 wParam。null → 0。</summary>
    public static IntPtr Encode(EdgeKind? edge) =>
        edge.HasValue ? new IntPtr((int)edge.Value + 1) : IntPtr.Zero;

    /// <summary>解码 wParam。0 / 负数 / 越界 / 未定义枚举 → null。</summary>
    public static EdgeKind? Decode(IntPtr wParam)
    {
        long raw = wParam.ToInt64();
        if (raw <= 0)
        {
            return null;
        }

        long value = raw - 1;
        if (value > int.MaxValue)
        {
            return null;
        }

        var edge = (EdgeKind)(int)value;
        return Enum.IsDefined(typeof(EdgeKind), edge) ? edge : null;
    }
}

/// <summary>
/// 全局粘贴的命中策略：纯函数、无 IO、无 WinForms 依赖，因此可以被单测完全覆盖。
/// </summary>
public static class PasteTargetPolicy
{
    /// <summary>
    /// 光标落在哪个展开态绳子上；不在任何绳子上返回 <c>null</c>（调用方回退到 <c>_activeEdge</c>）。
    ///
    /// <para>角上四条带会重叠，消歧规则（本轮修正）：在所有 <c>Bounds.Contains(cursor)</c> 的候选里，
    /// 取**光标到该边边界**的距离最小者（Top = cursor.Y − Bounds.Top，Left = cursor.X − Bounds.Left，
    /// Bottom / Right 对称）；距离完全相等时取 <see cref="EdgeKind"/> 枚举序更小者，
    /// 保证结果不随 targets 遍历顺序变化，同一份快照永远给出同一个边。</para>
    ///
    /// <para>为什么不用矩形中心距离：宽屏下中心的横坐标会主导距离，让贴着上边的光标被判给 Left
    /// （反例：1920×1080、光标 (40,2)，到 Top 中心约 1845px、到 Left 中心约 1077px）。</para>
    /// </summary>
    /// <param name="targets">快照里的展开态目标；null 或空 → null。</param>
    /// <param name="cursor">屏幕坐标（px）。</param>
    public static EdgeKind? HitExpandedLeash(IReadOnlyList<LeashTarget> targets, Point cursor)
    {
        if (targets is null || targets.Count == 0)
        {
            return null;
        }

        EdgeKind? best = null;
        long bestDistance = long.MaxValue;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (!target.Bounds.Contains(cursor))
            {
                continue;
            }

            // 距离用「光标到该边**边界**的距离」，而不是到矩形中心：
            // 宽屏下矩形中心的横坐标会主导距离，让贴着上边的光标被判给 Left
            // （反例：1920×1080、光标 (40,2)，到 Top 中心约 1845px、到 Left 中心约 1077px）。
            long distance = target.Edge switch
            {
                EdgeKind.Top => (long)cursor.Y - target.Bounds.Top,
                EdgeKind.Bottom => (long)target.Bounds.Bottom - cursor.Y,
                EdgeKind.Left => (long)cursor.X - target.Bounds.Left,
                _ => (long)target.Bounds.Right - cursor.X,
            };

            if (best is null
                || distance < bestDistance
                || (distance == bestDistance && (int)target.Edge < (int)best.Value))
            {
                bestDistance = distance;
                best = target.Edge;
            }
        }

        return best;
    }

    /// <summary>
    /// 是否由绳子接管这次粘贴。<paramref name="force"/> 是用户显式强制（例如钩子装好后的
    /// 「无论如何都挂到绳子」），它优先于前台是否有可粘贴目标。
    /// </summary>
    /// <param name="force">显式强制接管。</param>
    /// <param name="foregroundHasNoPasteTarget">前台确实没有可粘贴目标（见 <see cref="Interop.FocusProbe"/>）。</param>
    public static bool ShouldTakeOver(bool force, bool foregroundHasNoPasteTarget) =>
        force || foregroundHasNoPasteTarget;
}
