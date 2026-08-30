using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 跨處理器群組定址的純函式：全機序號 ↔ 群組＋群組內索引的換算、遮罩展開、標籤。
/// </summary>
/// <remarks>
/// Windows 的親和性遮罩是 <c>ULONG_PTR</c>，一個群組最多 64 個邏輯處理器；超過的機器
/// 會被切成多個群組，每組各自從位元 0 重新編號。1.5.x 一律用 <c>1UL &lt;&lt; lp</c> 釘選，
/// 在這種機器上只碰得到群組 0，其餘核心被<b>靜默</b>跳過。這組測試釘住換算，
/// 讓「群組 1 的 LP0」不會再被誤當成「群組 0 的 LP0」。
/// 實機的多群組行為無法在此驗證（開發機只有一個群組），所以純函式部分必須測得夠死。
/// </remarks>
public class CpuAffinityTests
{
    private static readonly int[] TwoGroups = [64, 64];       // 128 執行緒的雙路機
    private static readonly int[] Uneven = [64, 32];          // 群組大小不必相等
    private static readonly int[] Single = [36];              // 開發機（i9-7980XE）

    // ── 全機序號 → 群組＋索引 ────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(63, 0, 63)]
    [InlineData(64, 1, 0)]     // 關鍵那一格：跨到群組 1，索引重新從 0 算
    [InlineData(127, 1, 63)]
    public void 全機序號可拆成群組與群組內索引(int global, int group, int index)
    {
        var p = CpuAffinity.Split(global, TwoGroups);
        Assert.NotNull(p);
        Assert.Equal((ushort)group, p!.Value.Group);
        Assert.Equal(index, p.Value.Index);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(999)]
    [InlineData(-1)]
    public void 超出範圍的序號回空而不是看似合法的群組零(int global)
        => Assert.Null(CpuAffinity.Split(global, TwoGroups));

    [Fact]
    public void 群組大小不等時也照實切分()
    {
        Assert.Equal(new ProcessorRef(1, 31), CpuAffinity.Split(95, Uneven));
        Assert.Null(CpuAffinity.Split(96, Uneven));
    }

    [Fact]
    public void 空群組會被跳過而不是占掉編號()
    {
        int[] withHole = [4, 0, 4];
        Assert.Equal(new ProcessorRef(0, 3), CpuAffinity.Split(3, withHole));
        Assert.Equal(new ProcessorRef(2, 0), CpuAffinity.Split(4, withHole));
        Assert.Equal(new ProcessorRef(2, 3), CpuAffinity.Split(7, withHole));
        Assert.Null(CpuAffinity.Split(8, withHole));
    }

    // ── 群組＋索引 → 全機序號 ────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 63, 63)]
    [InlineData(1, 0, 64)]
    [InlineData(1, 63, 127)]
    public void 群組與索引可還原成全機序號(int group, int index, int expected)
        => Assert.Equal(expected, CpuAffinity.Global(new ProcessorRef((ushort)group, index), TwoGroups));

    [Theory]
    [InlineData(0, 64)]     // 索引超出該群組大小
    [InlineData(1, 32)]     // 群組 1 只有 32 顆
    [InlineData(2, 0)]      // 沒有群組 2
    [InlineData(0, -1)]
    public void 不存在的位置回空(int group, int index)
        => Assert.Null(CpuAffinity.Global(new ProcessorRef((ushort)group, index), Uneven));

    [Fact]
    public void 兩個方向互為反函式()
    {
        int total = Uneven.Sum();
        for (int g = 0; g < total; g++)
        {
            var p = CpuAffinity.Split(g, Uneven);
            Assert.NotNull(p);
            Assert.Equal(g, CpuAffinity.Global(p!.Value, Uneven));
        }
    }

    [Fact]
    public void 單一群組時序號就是索引()
    {
        for (int i = 0; i < 36; i++)
        {
            Assert.Equal(new ProcessorRef(0, i), CpuAffinity.Split(i, Single));
            Assert.Equal(i, CpuAffinity.Global(new ProcessorRef(0, i), Single));
        }
        Assert.Null(CpuAffinity.Split(36, Single));
    }

    // ── 遮罩 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 遮罩展開為由低到高的索引()
        => Assert.Equal(new[] { 0, 2, 5, 7 }, CpuAffinity.IndicesFromMask(0b1010_0101UL));

    [Fact]
    public void 空遮罩展開為空清單()
        => Assert.Empty(CpuAffinity.IndicesFromMask(0));

    [Fact]
    public void 全滿遮罩展開為六十四個索引()
    {
        var all = CpuAffinity.IndicesFromMask(ulong.MaxValue);
        Assert.Equal(64, all.Count);
        Assert.Equal(63, all[^1]);
    }

    [Fact]
    public void 最高位元不會因為帶符號位移而漏掉()
        => Assert.Equal(new[] { 63 }, CpuAffinity.IndicesFromMask(1UL << 63));

    // ── ProcessorRef ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1UL)]
    [InlineData(1, 2UL)]
    [InlineData(63, 1UL << 63)]
    public void 單位元遮罩落在群組內的正確位元(int index, ulong expected)
        => Assert.Equal(expected, new ProcessorRef(3, index).Mask);

    [Theory]
    [InlineData(64)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void 群組外的索引回零遮罩_呼叫端必須據此跳過(int index)
        => Assert.Equal(0UL, new ProcessorRef(0, index).Mask);

    [Fact]
    public void 多群組時標籤必須標明群組()
    {
        // 不標群組的話，兩個群組的「LP3」在畫面上長得一模一樣。
        Assert.Equal("G1·LP3", new ProcessorRef(1, 3).Label(true));
        Assert.Equal("LP3", new ProcessorRef(1, 3).Label(false));
    }

    [Fact]
    public void 不同群組的同號核心不是同一顆()
        => Assert.NotEqual(new ProcessorRef(0, 5), new ProcessorRef(1, 5));

    [Fact]
    public void 同群組同索引視為同一顆_可當字典鍵()
    {
        var map = new Dictionary<ProcessorRef, int> { [new ProcessorRef(2, 7)] = 42 };
        Assert.Equal(42, map[new ProcessorRef(2, 7)]);
        Assert.False(map.ContainsKey(new ProcessorRef(3, 7)));
    }

    // ── 實機查詢：只驗自洽，不假設數量 ───────────────────────────────────

    [Fact]
    public void 本機的群組數至少為一()
        => Assert.True(CpuAffinity.GroupCount >= 1);

    [Fact]
    public void 本機列舉出的核心數與各群組大小總和相符()
    {
        var sizes = CpuAffinity.GroupSizes();
        Assert.Equal(CpuAffinity.GroupCount, sizes.Length);
        var lps = CpuAffinity.AllLogicalProcessors();
        Assert.NotEmpty(lps);
        // 單一群組時會尊重行程親和性遮罩，故只能斷言不超過總數。
        Assert.True(lps.Count <= sizes.Sum());
        Assert.All(lps, p => Assert.True(p.Group < sizes.Length));
        Assert.All(lps, p => Assert.True(p.Index >= 0 && p.Index < 64));
    }

    [Fact]
    public void 本機列舉出的核心不重複()
    {
        var lps = CpuAffinity.AllLogicalProcessors();
        Assert.Equal(lps.Count, lps.Distinct().Count());
    }

    [Fact]
    public void 是否多群組與群組數一致()
        => Assert.Equal(CpuAffinity.GroupCount > 1, CpuAffinity.IsMultiGroup);

    [Fact]
    public void 釘選可還原_離開範圍後仍能跑在原本的核心上()
    {
        var lps = CpuAffinity.AllLogicalProcessors();
        var target = lps[^1];
        using (var pin = CpuAffinity.Pinned(target))
        {
            Assert.True(pin.Ok);
        }
        // 還原後仍可再次釘選（若 Dispose 把親和性弄壞，這一步會失敗）。
        using var again = CpuAffinity.Pinned(lps[0]);
        Assert.True(again.Ok);
    }

    [Fact]
    public void 零遮罩不會被送進系統呼叫()
    {
        using var pin = CpuAffinity.Pinned(new ProcessorRef(0, 64));   // Mask ＝ 0
        Assert.False(pin.Ok);
    }
}
