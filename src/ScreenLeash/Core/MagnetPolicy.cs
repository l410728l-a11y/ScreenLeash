namespace ScreenLeash.Core;

/// <summary>
/// 磁铁吸附高亮策略：高亮只用来提示「另一条绳子」这个落点，命中的是自己时不点亮。
///
/// <para><b>为什么不能点亮自己</b>：同边拖动排序时 <c>IsOverAnyLeash</c> 命中的就是自己；
/// 而单击卡片时手抖超过系统拖拽阈值（默认 4px）也会走进拖动判定，
/// 于是 <c>DrawSurface</c> 把整块窗口填成深蓝——用户看到的是「点一下面板闪一下蓝」。</para>
/// </summary>
public static class MagnetPolicy
{
    /// <summary>命中对象是否需要点亮。</summary>
    /// <param name="hit">吸附判定命中的绳子；null 表示没有命中。</param>
    /// <param name="self">发起拖动的绳子。</param>
    public static bool ShouldHighlight(object? hit, object? self)
        => hit is not null && !ReferenceEquals(hit, self);
}
