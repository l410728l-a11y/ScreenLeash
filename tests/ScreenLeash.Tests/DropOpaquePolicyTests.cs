using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 收起态「临时取消透明键」的判据。
///
/// <para><b>反例（用户实测反馈）</b>：在桌面或任何应用里点一下（哪怕完全不移动），
/// 屏幕右边就会闪出一个 40dip 宽的实色黑框，点一次闪一次。根因是旧判据只看
/// 「左键是否按下 && 未展开」——全屏任何一次左键按下都成立。</para>
/// </summary>
public class DropOpaquePolicyTests
{
    /// <summary>单击不移动：不能变不透明（这是用户报的那个 bug）。</summary>
    [Fact]
    public void 单击不移动_不变不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: false,
            dropHighlighted: false,
            draggingCard: false,
            cursorInsideWindow: true);

        Assert.False(opaque);
    }

    /// <summary>按住并移动、光标已经落在绳子上：这是在拖东西进来，需要不透明才能收 DragEnter。</summary>
    [Fact]
    public void 按住移动且光标在绳子上_变不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: true,
            dropHighlighted: false,
            draggingCard: false,
            cursorInsideWindow: true);

        Assert.True(opaque);
    }

    /// <summary>在别处按住拖动（拖图标、拖滚动条、框选）：光标不在绳子上，不该变不透明。</summary>
    [Fact]
    public void 按住移动但光标不在绳子上_不变不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: true,
            dropHighlighted: false,
            draggingCard: false,
            cursorInsideWindow: false);

        Assert.False(opaque);
    }

    /// <summary>松手立刻恢复透明，不残留。</summary>
    [Fact]
    public void 松手_立刻恢复透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: false,
            movedWhilePressed: true,
            dropHighlighted: true,
            draggingCard: false,
            cursorInsideWindow: true);

        Assert.False(opaque);
    }

    /// <summary>
    /// 449c0b0 的回归保护：外部东西已经拖进窗口（收到 DragEnter）时必须全程不透明，
    /// 否则展开→恢复透明→OLE 判定离开→收起→再 DragEnter，表现为「闪一下就缩回去」。
    /// </summary>
    [Fact]
    public void 外部拖入中_即使展开也保持不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: true,
            dropHighlighted: true,
            draggingCard: false,
            cursorInsideWindow: true);

        Assert.True(opaque);
    }

    /// <summary>展开态（悬停看卡片）点击不移动：不能变近黑面板。</summary>
    [Fact]
    public void 展开态点击不移动_不变不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: false,
            dropHighlighted: false,
            draggingCard: false,
            cursorInsideWindow: true);

        Assert.False(opaque);
    }

    /// <summary>拖自己的卡片排序：面板本身有卡片像素，不需要整块变黑。</summary>
    [Fact]
    public void 拖自己的卡片排序时_不变不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: true,
            dropHighlighted: false,
            draggingCard: true,
            cursorInsideWindow: true);

        Assert.False(opaque);
    }

    /// <summary>
    /// 外部东西拖到绳子上时，<c>EvaluateCursor</c> 会在同一 tick 先把窗口设成展开
    /// （光标进入 40dip 感应区就 SetState(Hover)）。如果判据把「展开」当作不变不透明的理由，
    /// 那拖到位的那一刻永远不变不透明 → OLE 命中不到窗口 → 黑框不出、挂不上。
    /// </summary>
    [Fact]
    public void 拖东西靠近导致展开时_仍必须变不透明()
    {
        bool opaque = DropOpaquePolicy.ShouldBeOpaque(
            leftDown: true,
            movedWhilePressed: true,
            dropHighlighted: false,
            draggingCard: false,
            cursorInsideWindow: true);

        Assert.True(opaque);
    }
}
