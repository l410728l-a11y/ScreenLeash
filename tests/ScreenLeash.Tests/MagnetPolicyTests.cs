using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 磁铁吸附高亮策略。
///
/// <para><b>反例（用户实测反馈）</b>：鼠标在面板上点一下（几乎不移动），
/// 面板会闪一下整块深蓝——单击手抖超过系统拖拽阈值被判成拖动，
/// 而同边拖动时吸附命中的就是自己，<c>SetMagnetHighlight(true)</c> 把整窗填成深蓝。</para>
/// </summary>
public class MagnetPolicyTests
{
    private sealed class FakeLeash
    {
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>命中自己（同边拖动排序）：不点亮——点亮自己没有任何「落点」含义。</summary>
    [Fact]
    public void 命中自己_不点亮()
    {
        var self = new FakeLeash { Name = "right" };

        Assert.False(MagnetPolicy.ShouldHighlight(self, self));
    }

    /// <summary>命中另一条绳子（跨边拖动）：点亮，告诉用户「松手放这里」。</summary>
    [Fact]
    public void 命中另一条绳子_点亮()
    {
        var self = new FakeLeash { Name = "right" };
        var other = new FakeLeash { Name = "left" };

        Assert.True(MagnetPolicy.ShouldHighlight(other, self));
    }

    /// <summary>没有命中任何绳子：不点亮。</summary>
    [Fact]
    public void 未命中_不点亮()
    {
        var self = new FakeLeash { Name = "right" };

        Assert.False(MagnetPolicy.ShouldHighlight(null, self));
    }
}
