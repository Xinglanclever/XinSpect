using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 韌體復原路徑的守門（ULTIMATE-1000 階段二 P2-061）。
///
/// <para>
/// <b>核心有三件事：</b>位元不命名、「讀不到」與「值為 0」分開、
/// 以及<b>復原區那一半的界線要說出「缺的是表不是實作」</b>。
/// </para>
/// </summary>
public class FirmwareRecoveryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static FirmwareRecoveryFactsService.RecoveryInput In(
        uint? indications, params string[] fvGuids)
        => new(indications, fvGuids);

    [Fact]
    public void 沒有讀取結果時要標不支援並說出不自己問韌體()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, null).Single();

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不自己去問韌體", f.UnavailableReason);
    }

    [Fact]
    public void 有值時要列出原始值與FV數()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, In(0x1F, "g1", "g2", "g3")).Single();

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("OsIndicationsSupported ＝ 0x000000000000001F", f.Value);
        Assert.Contains("映像內 3 個 FV", f.Value);
        Assert.Contains("未辨識復原區", f.Value);
        Assert.Equal(0x1Fu, f.NumericValue);
    }

    /// <summary>這一條守著「兩個不同的沒有」。</summary>
    [Fact]
    public void 讀不到與值為零要分開()
    {
        var missing = FirmwareRecoveryFactsService.Collect(At, In(null)).Single();
        var zero = FirmwareRecoveryFactsService.Collect(At, In(0)).Single();

        Assert.Equal(FactAvailability.NotSupported, missing.Availability);
        Assert.Null(missing.NumericValue);
        Assert.Contains("「讀不到」與「值為 0」是不同的斷言", missing.UnavailableReason);
        Assert.Contains("平台明確回報「不支援任何 OS 指示」", missing.UnavailableReason);

        Assert.Equal(FactAvailability.Present, zero.Availability);
        Assert.Equal(0.0, zero.NumericValue);
    }

    /// <summary>
    /// <b>這一條守著第五個「不命名」的判斷。</b>
    /// 位元不得被命名——即使是最有名的那些。
    /// </summary>
    [Fact]
    public void 位元不得被命名()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, In(0x1F)).Single();

        Assert.DoesNotContain("CAPSULE", f.Value);
        Assert.DoesNotContain("EFI_OS_INDICATIONS", f.Value);
        Assert.Contains("位元不命名，只列原始值", f.UnavailableReason);
        Assert.Contains("本專案沒有任何地方收錄那張表", f.UnavailableReason);
        Assert.Contains("填錯的位元會讓一個不存在的支援看起來存在", f.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著本項最重要的措辭。</b>
    /// 復原區那一半缺的是**一張表**，不是實作。
    /// </summary>
    [Fact]
    public void 復原區的界線要說出缺的是表不是實作()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, In(0x1F)).Single();

        Assert.Contains("復原區那一半本專案不做，理由不是「還沒寫」而是「沒有依據」", f.UnavailableReason);
        Assert.Contains("「復原區」不是一個有固定 GUID 的東西", f.UnavailableReason);
        Assert.Contains("它是**平台慣例**", f.UnavailableReason);
        Assert.Contains("沒有一張「平台慣例表」可以依據", f.UnavailableReason);
        Assert.Contains("缺的是一張表」，不是「缺的是實作", f.UnavailableReason);
    }

    [Fact]
    public void 復原區的界線要說出既有的UefiFv不知道哪個是復原區()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, In(0x1F)).Single();

        Assert.Contains("既有的 UefiFv 解的是**通用 FV**", f.UnavailableReason);
        Assert.Contains("它不知道哪一個是復原區", f.UnavailableReason);
    }

    [Fact]
    public void 讀不到時的理由也要包含復原區的界線()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, In(null)).Single();

        Assert.Contains("缺的是一張表」，不是「缺的是實作", f.UnavailableReason);
    }

    [Fact]
    public void 沒有FV時要如實報零個而不是留空()
    {
        var f = FirmwareRecoveryFactsService.Collect(At, In(0x1F)).Single();

        Assert.Contains("映像內 0 個 FV", f.Value);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var f = FirmwareRecoveryFactsService.Collect(At, In(0x1F)).Single();

        Assert.True(catalog.Contains(f.Key),
            $"韌體復原路徑產出了目錄裡沒有的鍵：{f.Key}");
    }
}
