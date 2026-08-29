using System.ComponentModel;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 天梯比對規則：名稱正規化（去品牌雜訊）、共同片段評分，以及榜單載入、
/// 桌機／筆電範圍過濾與本機高亮的實際行為（讀取內嵌 JSON，不觸碰硬體）。
/// </summary>
public class RankingMatchTests
{
    // ── Normalize：只留型號關鍵字 ─────────────────────────────────────────────

    [Fact]
    public void Normalize_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", RankingService.Normalize(null));
        Assert.Equal("", RankingService.Normalize("   "));
    }

    [Fact]
    public void Normalize_StripsIntelBrandNoise()
    {
        var n = RankingService.Normalize("Intel(R) Core(TM) i7-13700K CPU @ 3.40GHz");
        Assert.DoesNotContain("intel", n);
        Assert.DoesNotContain("(r)", n);
        Assert.Contains("i713700k", n);
    }

    [Fact]
    public void Normalize_StripsAmdBrandNoise()
    {
        var n = RankingService.Normalize("AMD Ryzen 7 7800X3D 8-Core Processor");
        Assert.DoesNotContain("amd", n);
        Assert.DoesNotContain("ryzen", n);
        Assert.DoesNotContain("processor", n);
        Assert.Contains("7800x3d", n);
    }

    [Fact]
    public void Normalize_StripsNvidiaBrandNoise()
        => Assert.Equal("rtx4070", RankingService.Normalize("NVIDIA GeForce RTX 4070"));

    [Fact]
    public void Normalize_DropsPunctuationAndCase()
        => Assert.Equal("rx7900xtx", RankingService.Normalize("AMD Radeon RX 7900 XTX"));

    // ── MatchScore：共同片段長度 ──────────────────────────────────────────────

    [Fact]
    public void MatchScore_EmptySide_IsZero()
    {
        Assert.Equal(0, RankingService.MatchScore("", "rtx4070"));
        Assert.Equal(0, RankingService.MatchScore("rtx4070", ""));
    }

    [Fact]
    public void MatchScore_Containment_ScoresShorterLength()
    {
        Assert.Equal(7, RankingService.MatchScore("rtx4070ti", "rtx4070"));
        Assert.Equal(7, RankingService.MatchScore("rtx4070", "rtx4070"));
    }

    [Fact]
    public void MatchScore_NoOverlap_IsZero()
        => Assert.Equal(0, RankingService.MatchScore("abc", "xyz"));

    [Fact]
    public void MatchScore_IsSymmetric()
        => Assert.Equal(RankingService.MatchScore("i713700k", "i713700kf"),
                        RankingService.MatchScore("i713700kf", "i713700k"));

    [Fact]
    public void MatchScore_DifferentGeneration_StaysBelowMarkThreshold()
    {
        // MarkBest 要求 >= 6 才標示本機；不同世代僅共用 "rtx" 三字元，不得誤標
        int s = RankingService.MatchScore(
            RankingService.Normalize("NVIDIA GeForce RTX 4070"),
            RankingService.Normalize("GeForce RTX 3060"));
        Assert.InRange(s, 1, 5);
    }

    [Fact]
    public void MatchScore_SameModelDifferentSuffix_ReachesMarkThreshold()
    {
        int s = RankingService.MatchScore(
            RankingService.Normalize("Intel(R) Core(TM) i7-13700K CPU @ 3.40GHz"),
            RankingService.Normalize("Core i7-13700K"));
        Assert.True(s >= 6, $"同型號的比對分數 {s} 應達到標示門檻 6");
    }
}

/// <summary>
/// 天梯榜單本體：內嵌 JSON 的載入、桌機／筆電過濾、搜尋與本機高亮。
/// 僅建立服務物件（ListCollectionView 於當前執行緒建立與讀取），不需視窗。
/// </summary>
public class RankingServiceTests
{
    private static List<RankRow> Rows(ICollectionView v) => v.Cast<RankRow>().ToList();

    [Fact]
    public void EmbeddedData_LoadsBothLadders()
    {
        var svc = new RankingService();
        Assert.NotEmpty(Rows(svc.CpuList));
        Assert.NotEmpty(Rows(svc.GpuList));
        Assert.StartsWith("資料來源：", svc.CpuSource);
        Assert.StartsWith("資料來源：", svc.GpuSource);
    }

    [Fact]
    public void DefaultScope_ShowsDesktopOnly()
    {
        var svc = new RankingService();
        Assert.All(Rows(svc.CpuList), r => Assert.False(r.IsLaptop));
        svc.CpuScope = 1;
        var laptop = Rows(svc.CpuList);
        Assert.NotEmpty(laptop);
        Assert.All(laptop, r => Assert.True(r.IsLaptop));
    }

    [Fact]
    public void Filter_IsCaseInsensitiveSubstring()
    {
        var svc = new RankingService();
        var first = Rows(svc.GpuList)[0].Name;
        svc.GpuFilter = first.ToLowerInvariant();
        Assert.Contains(Rows(svc.GpuList), r => r.Name == first);

        svc.GpuFilter = "絕不存在的型號";
        Assert.Empty(Rows(svc.GpuList));
    }

    [Fact]
    public void Highlight_MarksMatchingRow()
    {
        var svc = new RankingService();
        // 取一列正規化後長度足夠的型號（MarkBest 對過短的鍵一律略過）
        var target = Rows(svc.CpuList).First(r => RankingService.Normalize(r.Name).Length >= 6).Name;
        svc.Highlight(target, null);
        Assert.Contains(Rows(svc.CpuList), r => r.IsLocal);
    }

    [Fact]
    public void Highlight_IgnoresNullAndTooShortNames()
    {
        var svc = new RankingService();
        svc.Highlight(null, null);
        svc.Highlight("ab", "x");
        Assert.DoesNotContain(Rows(svc.CpuList), r => r.IsLocal);
        Assert.DoesNotContain(Rows(svc.GpuList), r => r.IsLocal);
    }

    [Fact]
    public void Highlight_UnknownHardware_MarksNothing()
    {
        var svc = new RankingService();
        svc.Highlight("Qqzzxx Vvwwyy Processor", "Qqzzxx Vvwwyy Graphics");
        Assert.DoesNotContain(Rows(svc.CpuList), r => r.IsLocal);
        Assert.DoesNotContain(Rows(svc.GpuList), r => r.IsLocal);
    }
}
