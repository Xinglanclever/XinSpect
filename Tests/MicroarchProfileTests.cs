using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 微架構查表：CPUID 簽章拆解、混合架構核型判定、管線寬度與 Top-down 方法。
/// </summary>
/// <remarks>
/// 這組測試存在的理由是一個具體的正確性問題：TMA Level 1 的分母是
/// <c>管線寬度 × CPU_CLK_UNHALTED</c>，寬度寫死 4 會讓 Ice Lake 之後的機器整組比例偏大，
/// 而畫面上看起來仍然合理。因此寬度必須逐代釘住，而「不在表內」必須確實回到
/// <see cref="TmaMethod.None"/>——寧可不出數字，也不要猜一個分母。
/// </remarks>
public class MicroarchProfileTests
{
    // ── CPUID 簽章拆解 ────────────────────────────────────────────────────

    [Fact]
    public void 簽章拆解_家族是相加而不是相接()
    {
        // i9-7980XE（Skylake-X）的實際簽章 0x00050654：Family 6、Model 0x55。
        // Family ＝ base[11:8] ＋ extended[27:20]；寫成「相接」會得到 0x06 之外的怪值，型號整批對不上。
        var (family, model) = MicroarchProfile.DecodeSignature(0x00050654);
        Assert.Equal(6, family);
        Assert.Equal(0x55, model);
    }

    [Theory]
    [InlineData(0x000906EAu, 6, 0x9E)]   // Coffee Lake
    [InlineData(0x000806C1u, 6, 0x8C)]   // Tiger Lake
    [InlineData(0x00090672u, 6, 0x97)]   // Alder Lake-S（大核 ＋ 小核）
    [InlineData(0x000906A4u, 6, 0x9A)]   // Alder Lake-P：低位 0xA ＋ 擴充 0x9，不是 0x97
    [InlineData(0x000806F8u, 6, 0x8F)]   // Sapphire Rapids
    public void 簽章拆解_型號由高低兩段拼成(uint sig, int family, int model)
    {
        var (f, m) = MicroarchProfile.DecodeSignature(sig);
        Assert.Equal(family, f);
        Assert.Equal(model, m);
    }

    [Fact]
    public void 簽章拆解_擴充家族確實加上去()
    {
        // AMD Zen 的 0x00800F10：base family 0xF ＋ extended 0x08 ＝ 23（Family 17h）。
        var (family, _) = MicroarchProfile.DecodeSignature(0x00800F10);
        Assert.Equal(23, family);
    }

    // ── 混合架構核型 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x40000000u, CoreKind.Performance)]
    [InlineData(0x20000000u, CoreKind.Efficiency)]
    [InlineData(0x00000000u, CoreKind.Unknown)]   // leaf 不存在
    [InlineData(0x30000000u, CoreKind.Unknown)]   // 未定義編碼不硬猜
    public void 核型由CPUID1A的最高位元組判定(uint eax, CoreKind expected)
        => Assert.Equal(expected, MicroarchProfile.CoreKindFromCpuid1A(eax));

    [Fact]
    public void 核型有可顯示的中文標籤()
    {
        Assert.Contains("大核", MicroarchProfile.CoreKindText(CoreKind.Performance));
        Assert.Contains("小核", MicroarchProfile.CoreKindText(CoreKind.Efficiency));
        Assert.Equal("單一核型", MicroarchProfile.CoreKindText(CoreKind.Unknown));
    }

    // ── 管線寬度：逐代釘住 ────────────────────────────────────────────────

    [Theory]
    [InlineData(0x2A, 4)]   // Sandy Bridge
    [InlineData(0x3C, 4)]   // Haswell
    [InlineData(0x4E, 4)]   // Skylake
    [InlineData(0x55, 4)]   // Skylake-X／Cascade Lake-SP（本機）
    [InlineData(0x9E, 4)]   // Coffee Lake
    [InlineData(0xA5, 4)]   // Comet Lake
    [InlineData(0x7E, 5)]   // Ice Lake（Sunny Cove）
    [InlineData(0x6A, 5)]   // Ice Lake-SP
    [InlineData(0x8C, 5)]   // Tiger Lake（Willow Cove）
    [InlineData(0xA7, 5)]   // Rocket Lake（Cypress Cove）
    [InlineData(0x8F, 6)]   // Sapphire Rapids（Golden Cove）
    [InlineData(0xCF, 6)]   // Emerald Rapids（Raptor Cove）
    [InlineData(0xAD, 6)]   // Granite Rapids（Redwood Cove）
    public void 管線寬度逐代不同_不得退化成常數四(int model, int expectedWidth)
        => Assert.Equal(expectedWidth, MicroarchProfile.Identify(6, model).PipelineWidth);

    [Fact]
    public void 混合部件依核型分流出不同寬度與不同方法()
    {
        var pAlder = MicroarchProfile.Identify(6, 0x97, CoreKind.Performance);
        var eAlder = MicroarchProfile.Identify(6, 0x97, CoreKind.Efficiency);
        Assert.Equal("Golden Cove", pAlder.Uarch);
        Assert.Equal(6, pAlder.PipelineWidth);
        Assert.Equal(TmaMethod.PerfMetrics, pAlder.Tma);
        Assert.Equal("Gracemont", eAlder.Uarch);
        Assert.Equal(5, eAlder.PipelineWidth);
        Assert.Equal(TmaMethod.AtomTopdown, eAlder.Tma);
        Assert.True(pAlder.IsHybrid && eAlder.IsHybrid);

        var pLunar = MicroarchProfile.Identify(6, 0xBD, CoreKind.Performance);
        var eLunar = MicroarchProfile.Identify(6, 0xBD, CoreKind.Efficiency);
        Assert.Equal("Lion Cove", pLunar.Uarch);
        Assert.Equal(8, pLunar.PipelineWidth);
        Assert.Equal("Skymont", eLunar.Uarch);
        Assert.Equal(8, eLunar.PipelineWidth);
    }

    [Fact]
    public void 只有小核的部件不論核型參數都是小核()
    {
        // Alder Lake-N（0xBE）沒有大核；CPUID 0x1A 讀不到時也不能誤判成 Golden Cove。
        foreach (var kind in new[] { CoreKind.Unknown, CoreKind.Performance, CoreKind.Efficiency })
            Assert.Equal("Gracemont", MicroarchProfile.Identify(6, 0xBE, kind).Uarch);
    }

    // ── Top-down 方法：能不能出數字 ───────────────────────────────────────

    [Theory]
    [InlineData(0x55)]   // Skylake-X
    [InlineData(0x3C)]   // Haswell
    [InlineData(0x7E)]   // Ice Lake
    [InlineData(0xA7)]   // Rocket Lake
    public void 事件式Level1可用的架構(int model)
    {
        var info = MicroarchProfile.Identify(6, model);
        Assert.Equal(TmaMethod.LegacyEvents, info.Tma);
        Assert.True(info.LegacyTmaUsable);
    }

    [Theory]
    [InlineData(0x1A)]   // Nehalem：IDQ_UOPS_NOT_DELIVERED 尚不存在
    [InlineData(0x2F)]   // Westmere
    [InlineData(0x8F)]   // Sapphire Rapids：改走 PERF_METRICS
    [InlineData(0x5C)]   // Goldmont：Atom 事件族
    [InlineData(0xAF)]   // Sierra Forest
    public void 不適用事件式配方的架構一律拒絕出數字(int model)
    {
        var info = MicroarchProfile.Identify(6, model);
        Assert.False(info.LegacyTmaUsable);
        Assert.NotEqual(TmaMethod.LegacyEvents, info.Tma);
    }

    [Fact]
    public void 每一種方法都有可以直接顯示的具體原因()
    {
        foreach (int model in new[] { 0x55, 0x8F, 0x5C, 0x1A })
            Assert.False(string.IsNullOrWhiteSpace(MicroarchProfile.Identify(6, model).TmaNote));
        Assert.False(string.IsNullOrWhiteSpace(MicroarchProfile.Unknown.TmaNote));
    }

    // ── 不在表內：誠實說不知道 ────────────────────────────────────────────

    [Theory]
    [InlineData(6, 0x00)]
    [InlineData(6, 0xFE)]
    [InlineData(6, 0x123)]
    public void 未列出的Intel型號回未知而不是就近取一個寬度(int family, int model)
    {
        var info = MicroarchProfile.Identify(family, model);
        Assert.False(info.IsKnown);
        Assert.Equal(0, info.PipelineWidth);
        Assert.Equal(TmaMethod.None, info.Tma);
        Assert.Equal("未知微架構", info.DisplayName);
    }

    [Theory]
    [InlineData(15, 0x02)]   // Pentium 4
    [InlineData(23, 0x01)]   // AMD Zen
    [InlineData(25, 0x21)]   // AMD Zen 3
    [InlineData(19, 0x01)]   // Intel Family 19（本表未涵蓋）
    public void 非Intel家族六一律回未知_同號事件意義不同不能套(int family, int model)
        => Assert.False(MicroarchProfile.Identify(family, model).IsKnown);

    [Fact]
    public void 未知常數本身就是拒絕出數字的樣子()
    {
        Assert.False(MicroarchProfile.Unknown.IsKnown);
        Assert.False(MicroarchProfile.Unknown.LegacyTmaUsable);
        Assert.Equal(0, MicroarchProfile.Unknown.PipelineWidth);
    }

    // ── 顯示字串 ──────────────────────────────────────────────────────────

    [Fact]
    public void 顯示名含產品代號微架構與寬度()
    {
        string name = MicroarchProfile.Identify(6, 0x55).DisplayName;
        Assert.Contains("Skylake", name);
        Assert.Contains("4 插槽／周期", name);
    }

    [Fact]
    public void 產品代號與微架構同名時不重複寫兩次()
    {
        string name = MicroarchProfile.Identify(6, 0x4E).DisplayName;   // Skylake／Skylake
        Assert.StartsWith("Skylake，", name);
        Assert.DoesNotContain("（Skylake）", name);
    }
}
