using ScreenLeash.Interop;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 前台焦点黑名单的纯判定测试（不触 Win32，只测类名归类）。
/// 语义是**保守**的：只有明确「这里 Ctrl+V 没意义」才接管，没见过的类名一律放行。
/// </summary>
public class FocusProbeTests
{
    // 桌面本体：Ctrl+V 无处可去 → 接管。
    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("SHELLDLL_DefView")]
    [InlineData("")]     // 空焦点（拿不到类名之外的「无焦点」路径）
    [InlineData(null)]   // null = 无焦点句柄
    public void NoPasteTarget_桌面与空焦点_接管(string? className)
    {
        Assert.True(FocusProbe.IsNoPasteTargetClass(className));
    }

    // 真实可编辑/可接收粘贴的宿主：必须放行，否则用户正常粘贴会被绳子吞掉。
    [Theory]
    [InlineData("Edit")]                      // 记事本
    [InlineData("Chrome_RenderWidgetHostHWND")] // 浏览器内容区
    [InlineData("WeChatMainWndForPC")]        // 微信主窗
    [InlineData("CabinetWClass")]             // 资源管理器（Ctrl+V = 粘贴文件，必须留给系统）
    public void HasPasteTarget_可粘贴宿主_放行(string className)
    {
        Assert.False(FocusProbe.IsNoPasteTargetClass(className));
    }

    // 类名大小写不敏感：Win32 类名的写法在不同应用里并不统一。
    [Fact]
    public void 类名大小写不敏感()
    {
        Assert.True(FocusProbe.IsNoPasteTargetClass("progman"));
        Assert.True(FocusProbe.IsNoPasteTargetClass("WORKERW"));
        Assert.False(FocusProbe.IsNoPasteTargetClass("cAbInEtWcLaSs"));
    }

    // 没见过的类名一律放行（黑名单的代价控制：宁可粘贴没管，不可误吞）。
    [Fact]
    public void 未知类名_放行()
    {
        Assert.False(FocusProbe.IsNoPasteTargetClass("SomeBrandNewControlClass"));
    }
}
