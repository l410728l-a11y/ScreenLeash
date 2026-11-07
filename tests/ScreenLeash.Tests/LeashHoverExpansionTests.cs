using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using ScreenLeash.Core;
using ScreenLeash.UI;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 悬停展开判据：拖着东西靠近屏幕边缘时，面板不该跟着展开。
///
/// <para><b>反例（用户实测反馈）</b>：从屏幕中间拖文件到右边，绳子整块面板弹出 140dip
/// （"整体框都会弹出一百多的距离"），挡住正要拖的东西。根因是 <c>EvaluateCursor</c> 把
/// 「按住左键拖着东西靠近」当成「悬停」，走同一条 SetState(Hover) 展开路径。</para>
///
/// <para>正确行为：外部东西拖近时只把绳线点亮 / 用 40dip 不透明接收区接住，不展开面板；
/// 松手后照常「收起 → 需要时再悬停展开」。</para>
/// </summary>
public class LeashHoverExpansionTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly EdgeSlot _slot;

    public LeashHoverExpansionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "leash-hover-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        _slot = new EdgeSlot(
            new ScreenInfo(
                "test",
                new Rectangle(0, 0, 1920, 1080),
                new Rectangle(0, 0, 1920, 1040),
                96,
                Primary: true),
            EdgeKind.Right);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 从屏幕中间（距右边 920dip，超出 280dip 冻结区）按下左键，再把光标拖到绳子上：
    /// 这是「从远处拖东西过来」的轨迹，面板不应展开。
    /// </summary>
    [Fact]
    public void 按住拖动经过绳子_不展开()
    {
        RunOnSta(() =>
        {
            using var store = new ItemStore(_paths);
            using var form = new LeashForm(_slot, store, null, _paths, new AppSettings());

            form.EvaluateCursor(new Point(1000, 500), leftButtonDown: true, mayBeDragging: false);
            Assert.False(form.IsExpanded);

            form.EvaluateCursor(new Point(1900, 500), leftButtonDown: true, mayBeDragging: true);

            Assert.False(form.IsExpanded);
        });
    }

    /// <summary>没有按键、只是把鼠标移过去：仍然要展开（悬停查看卡片）。</summary>
    [Fact]
    public void 普通悬停_仍然展开()
    {
        RunOnSta(() =>
        {
            using var store = new ItemStore(_paths);
            using var form = new LeashForm(_slot, store, null, _paths, new AppSettings());

            form.EvaluateCursor(new Point(1900, 500), leftButtonDown: false, mayBeDragging: false);

            Assert.True(form.IsExpanded);
        });
    }

    /// <summary>按在卡片上拖排序：窗口本来就该保持展开，不能被「正在拖」的判据收起来。</summary>
    [Fact]
    public void 在卡片上按住拖动_保持展开()
    {
        RunOnSta(() =>
        {
            using var store = new ItemStore(_paths);
            using var form = new LeashForm(_slot, store, null, _paths, new AppSettings());

            // 先悬停展开，再模拟「按在卡片上」（_pressedIndex >= 0）。
            form.EvaluateCursor(new Point(1900, 500), leftButtonDown: false, mayBeDragging: false);
            SetField(form, "_pressedIndex", 0);

            form.EvaluateCursor(new Point(1900, 500), leftButtonDown: true, mayBeDragging: true);

            Assert.True(form.IsExpanded);
        });
    }

    private static void SetField(LeashForm form, string field, object value)
    {
        var info = typeof(LeashForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new MissingFieldException(nameof(LeashForm), field);
        info.SetValue(form, value);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
