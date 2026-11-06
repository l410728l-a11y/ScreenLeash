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
/// 内部拖动（在绳子上拖卡片换位置）结束后的状态清理。
///
/// <para><b>反例（用户实测反馈）</b>：把卡片换个位置松手后，收起态窗口变成一整条
/// 约 40dip 宽的实色残留，而且再也不消失。原因是拖动中吸附到的「磁铁目标」高亮没有被关掉
/// （<c>SetDragTarget(this)</c> 点亮了自己，退出路径只把 <c>_dragTargetForm</c> 置 null），
/// 于是 <c>DrawSurface</c> 的 <c>shouldDraw</c> 永远为真，每帧把整个窗口填成底色。</para>
///
/// <para><b>为什么测内部状态而不是截图</b>：真实症状发生在没有句柄的贴边窗口上，
/// 自动化抓它的像素需要可见桌面与真实鼠标拖拽；这里测的是症状上游的**状态清理契约**——
/// 松手后 <c>_magnetHighlight</c> 必须为 false、目标引用必须为 null。</para>
/// </summary>
public class LeashDragCleanupTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public LeashDragCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "leash-drag-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
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
    /// 同边拖动排序：光标没离开吸附区时 <c>IsOverAnyLeash</c> 命中的就是自己，
    /// 于是 <c>SetDragTarget(this)</c> 点亮了本窗口的磁铁高亮。松手后必须关掉。
    /// </summary>
    [Fact]
    public void 内部拖动松手后_磁铁高亮必须关掉()
    {
        RunOnSta(() =>
        {
            var slot = new EdgeSlot(
                new ScreenInfo(
                    "test",
                    new Rectangle(0, 0, 1920, 1080),
                    new Rectangle(0, 0, 1920, 1040),
                    96,
                    Primary: true),
                EdgeKind.Right);

            using var store = new ItemStore(_paths);
            using var form = new LeashForm(slot, store, null, _paths, new AppSettings());

            // 拖动中那一帧：吸附到自己。
            InvokePrivate(form, "SetDragTarget", form);
            Assert.True(ReadField<bool>(form, "_magnetHighlight"), "前置条件：拖动中的磁铁高亮应已点亮。");

            // 松手：PollDrag 走 !leftDown 分支。
            SetField(form, "_internalDrag", true);
            form.PollDrag(new Point(1900, 500), leftDown: false);

            Assert.False(
                ReadField<bool>(form, "_magnetHighlight"),
                "松手后磁铁高亮没关：DrawSurface 会每帧填满收起态窗口，留下 40dip 宽的实色残留。");
            Assert.Null(ReadField<object?>(form, "_dragTargetForm"));
        });
    }

    /// <summary>
    /// 捕获被系统抢走（Alt+Tab / 别的窗口抢焦点）时也要清掉，否则同上。
    /// </summary>
    [Fact]
    public void 拖动中丢失鼠标捕获后_磁铁高亮必须关掉()
    {
        RunOnSta(() =>
        {
            var slot = new EdgeSlot(
                new ScreenInfo(
                    "test",
                    new Rectangle(0, 0, 1920, 1080),
                    new Rectangle(0, 0, 1920, 1040),
                    96,
                    Primary: true),
                EdgeKind.Right);

            using var store = new ItemStore(_paths);
            using var form = new LeashForm(slot, store, null, _paths, new AppSettings());

            InvokePrivate(form, "SetDragTarget", form);
            SetField(form, "_internalDrag", true);

            // Capture 已经是 false（未 Show 的窗口本来就抓不到捕获），事件处理器按此清理。
            InvokePrivate(form, "OnMouseCaptureChanged", EventArgs.Empty);

            Assert.False(ReadField<bool>(form, "_magnetHighlight"));
            Assert.Null(ReadField<object?>(form, "_dragTargetForm"));
        });
    }

    private static void InvokePrivate(LeashForm form, string method, params object?[] args)
    {
        var info = typeof(LeashForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new MissingMethodException(nameof(LeashForm), method);
        info.Invoke(form, args);
    }

    private static T ReadField<T>(LeashForm form, string field)
    {
        var info = typeof(LeashForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new MissingFieldException(nameof(LeashForm), field);
        return (T)info.GetValue(form)!;
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
