using System;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// EFI 變數面三把鍵（P2-082／P2-089／P2-090）的守門。
///
/// <para>
/// <b>這三把的答案就是「平台不提供」。</b>守門要盯的不是「有沒有讀到值」——那永遠讀不到——
/// 而是三件更容易壞掉的事：<b>兩個「沒有」要分得開</b>（Legacy BIOS 開機時是「本機不適用」，
/// 不是「平台不支援」）、<b>理由要說得出是缺什麼</b>（列舉 API 不存在，不是權限不足）、
/// 以及<b>非 Present 不得帶數字</b>。
/// </para>
/// </summary>
public class FirmwareVariableSurfaceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(int? firmwareType, string key)
        => FirmwareVariableSurfaceFactsService.Collect(At, firmwareType).Single(f => f.Key == key);

    [Fact]
    public void UEFI開機時_三把都是平台不支援並說出缺的是列舉API()
    {
        var facts = FirmwareVariableSurfaceFactsService.Collect(At, 2);

        Assert.Equal(3, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotSupported, f.Availability);
            Assert.Null(f.NumericValue);                       // 非 Present 不得帶數字
            Assert.Contains("沒有列舉 API", f.UnavailableReason);
            Assert.Contains("SeSystemEnvironmentPrivilege", f.UnavailableReason);   // 第二個前提也寫出來
        });
    }

    [Fact]
    public void LegacyBIOS開機時是不適用_不是不支援()
    {
        // 「這台機器沒有 UEFI 介面」與「有 UEFI 但列舉不了」是兩個不同的沒有——
        // 壓成同一個會讓讀的人以為換一台機器就能看到值。
        var facts = FirmwareVariableSurfaceFactsService.Collect(At, 1);

        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Contains("Legacy BIOS", f.UnavailableReason);
            Assert.DoesNotContain("沒有列舉 API", f.UnavailableReason);
        });
    }

    [Fact]
    public void 韌體型別未知時_仍然說得出卡在列舉而不是推給型別()
    {
        var f = Fact(null, FirmwareVariableSurfaceFactsService.CountKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("韌體型別也未能確認", f.UnavailableReason);
        Assert.Contains("沒有列舉 API", f.UnavailableReason);
    }

    [Fact]
    public void 三把鍵都在編譯期目錄裡_而且名稱與規格一致()
    {
        string[] keys =
        [
            FirmwareVariableSurfaceFactsService.CountKey,
            FirmwareVariableSurfaceFactsService.CapsuleKey,
            FirmwareVariableSurfaceFactsService.NvramKey,
        ];

        Assert.Equal(["ev.var.count", "fw.capsule.history", "ev.nvram.usage"], keys);
        Assert.All(keys, k => Assert.Contains(k, FactKeyCatalog.Keys));
    }

    [Fact]
    public void 三把鍵互不重複且來源一致()
    {
        var facts = FirmwareVariableSurfaceFactsService.Collect(At, 2);

        Assert.Equal(3, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(facts.Select(f => f.Source).Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// 接線：這三把不是「寫好服務放著」。
    /// <para>
    /// 這一條讀原始碼而不是跑 <see cref="EvidenceLabService.AllFacts"/>：後者要真的建一條驅動後端
    /// 收集路徑才跑得起來，而<b>要釘住的性質是「有沒有人呼叫」</b>——那正是
    /// <c>WiringGuardTests</c> 的同一條線（服務寫好了、單元測試也綠，不代表有人叫它）。
    /// 並且要求收集點與 <c>fw.mode</c> 相鄰：兩份事實必須來自同一次韌體型別讀取，
    /// 分頭去讀就會出現「同一輪裡兩種韌體型別」這種自我矛盾的輸出。
    /// </para>
    /// </summary>
    [Fact]
    public void 這三把鍵真的被收集段的AllFacts接上()
    {
        string root = RepoRoot();
        string text = File.ReadAllText(Path.Combine(root, "Services", "EvidenceLabService.cs"));

        Assert.Contains("FirmwareVariableSurfaceFactsService.Collect", text);
        Assert.Contains(".Concat(FirmwareVariableSurfaceFacts)", text);

        // 韌體型別沿用 fw.mode 那次讀取的結果：收集點必須在 FirmwareModeFactsService.Read() 之後，
        // 而且 fwModeInputs 要是同一個變數（分頭讀＝同一輪可能有兩種型別）。
        int readIdx = text.IndexOf("var fwModeInputs = FirmwareModeFactsService.Read", StringComparison.Ordinal);
        int surfaceIdx = text.IndexOf("FirmwareVariableSurfaceFactsService.Collect", StringComparison.Ordinal);
        Assert.True(readIdx >= 0, "找不到 fw.mode 的讀取點——這一項的前提是它把型別一起傳下去");
        Assert.True(surfaceIdx > readIdx, "EFI 變數面必須接在韌體型別讀取之後");
        Assert.Contains("fwModeInputs.FirmwareType", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
