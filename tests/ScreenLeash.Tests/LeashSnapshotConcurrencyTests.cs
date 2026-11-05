using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 快照的并发发布协议测试：钩子线程读、UI 线程写，必须读到**整体一致**的一份快照。
/// 反例（本测试要挡住的）：直接改写共享数组/字段 → 读到半个 Rectangle、或两条边的矩形拼接混在一起。
/// 保证手段：快照不可变 + <c>Volatile.Write/Read</c> 引用赋值（引用赋值原子）。
/// </summary>
public class LeashSnapshotConcurrencyTests
{
    // ─── ⑧ 10 万次读写无混合值 ─────────────────────────────────────
    [Fact]
    public async Task 快照并发十万次读写_不出现混合值()
    {
        // A 与 B 的矩形**互不重叠**，任何「拼接混合」都会让命中判定落到第三条边，从而被抓到。
        var snapshotA = new LeashSnapshot(new[]
        {
            new LeashTarget(EdgeKind.Left, new Rectangle(0, 0, 40, 1080)),
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 1920, 40)),
        });
        var snapshotB = new LeashSnapshot(new[]
        {
            new LeashTarget(EdgeKind.Right, new Rectangle(1880, 0, 40, 1080)),
            new LeashTarget(EdgeKind.Bottom, new Rectangle(0, 1040, 1920, 40)),
        });

        // 探针点必须避开角落：角落会触发消歧规则，与本测试要验证的「快照是否完整」无关。
        // 取 X 贴边、Y 居中 → 只可能命中 Left（A）或 Right（B）。
        var leftTopCursor = new Point(5, 500);        // 只落在 A 的 Left 带（Y=500 不在 Top 的 40 高内）
        var rightBottomCursor = new Point(1900, 500); // 只落在 B 的 Right 带

        var slot = new LeashSnapshotSlot();
        slot.Publish(snapshotA);

        const int Iterations = 100_000;
        int stop = 0;

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                slot.Publish((i++ & 1) == 0 ? snapshotB : snapshotA);
            }
        });

        int sawA = 0;
        int sawB = 0;
        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                var current = slot.Current;

                if (ReferenceEquals(current, snapshotA))
                {
                    sawA++;
                    Assert.Equal(2, current.Count);
                    Assert.Equal(new[] { EdgeKind.Left, EdgeKind.Top }, current.Targets.Select(t => t.Edge).ToArray());
                    Assert.Equal(EdgeKind.Left, current.HitTest(leftTopCursor));
                    Assert.Null(current.HitTest(rightBottomCursor));
                    continue;
                }

                if (ReferenceEquals(current, snapshotB))
                {
                    sawB++;
                    Assert.Equal(2, current.Count);
                    Assert.Equal(new[] { EdgeKind.Right, EdgeKind.Bottom }, current.Targets.Select(t => t.Edge).ToArray());
                    Assert.Equal(EdgeKind.Right, current.HitTest(rightBottomCursor));
                    Assert.Null(current.HitTest(leftTopCursor));
                    continue;
                }

                Assert.Fail($"读到了既非 A 也非 B 的实例（疑似读到写了一半的状态）：{current}");
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await writer;
        }

        // 读写确实并发发生过（否则本测试只是单线程空转）。
        Assert.True(sawA + sawB == Iterations);
        Assert.True(sawA > 0 && sawB > 0, $"读写未真正交错：sawA={sawA}, sawB={sawB}");
    }

    // ─── 初始状态与空发布 ─────────────────────────────────────────
    [Fact]
    public void 未发布时是空快照_命中为null()
    {
        var slot = new LeashSnapshotSlot();

        Assert.Same(LeashSnapshot.Empty, slot.Current);
        Assert.Equal(0, slot.Current.Count);
        Assert.Null(slot.Current.HitTest(new Point(0, 0)));
    }

    [Fact]
    public void 发布null回退为空快照()
    {
        var slot = new LeashSnapshotSlot();
        slot.Publish(new LeashSnapshot(new[] { new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 100, 50)) }));

        slot.Publish(null!);

        Assert.Same(LeashSnapshot.Empty, slot.Current);
        Assert.Null(slot.Current.HitTest(new Point(10, 10)));
    }

    [Fact]
    public void 快照自身命中与策略函数一致()
    {
        var targets = new[]
        {
            new LeashTarget(EdgeKind.Left, new Rectangle(0, 0, 40, 1080)),
            new LeashTarget(EdgeKind.Top, new Rectangle(0, 0, 1920, 40)),
        };
        var snapshot = new LeashSnapshot(targets);
        var cursor = new Point(10, 10);

        Assert.Equal(PasteTargetPolicy.HitExpandedLeash(targets, cursor), snapshot.HitTest(cursor));
    }
}
