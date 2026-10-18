using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 韌體風險陳述的守門（ULTIMATE-1000 階段二 P2-099～P2-104，鍵 <c>fw.risk.facts.*</c>）。
///
/// <para>
/// 這一組守的<b>不是數字對不對，而是這一套敘述有沒有越界</b>：
/// ①等級的界線只有「有沒有」，不得為了好看而變成分數；
/// ②缺口不得被當成 0 或「沒問題」；
/// ③基線不含時間與機器識別（否則雜湊每次都變，離線比對就沒有意義）；
/// ④差異只比對參與風險陳述的鍵，跨機器要拒比；
/// ⑤整組不得自己新增任何讀取路徑——來源是同一輪已經收集好的事實。
/// </para>
///
/// <para>
/// 測試用<b>合成</b>的 <see cref="FactRelationRow"/> 直接驅動服務（記錄的建構子是公開的），
/// 所以四個等級分支、缺口的有無、差異的三種結果都能在不碰硬體的情況下驗到。
/// </para>
/// </summary>
public class FirmwareRiskFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    // ── 合成輸入 ──────────────────────────────────────────────────────────

    private static FactRelationRow Row(string id, FactRelation relation, string reason = "合成：一致")
        => new(id, $"規則 {id}", relation, reason, "合成規則（測試用）");

    private static List<FactRelationRow> Rows(int consistent, int contradicts, int unverifiable)
        => Enumerable.Range(0, consistent).Select(i => Row($"r{i}", FactRelation.Consistent))
            .Concat(Enumerable.Range(0, contradicts).Select(i => Row($"c{i}", FactRelation.Contradicts, "合成：兩側不一致")))
            .Concat(Enumerable.Range(0, unverifiable).Select(i => Row($"u{i}", FactRelation.Unverifiable, "輸入事實讀不到：tpm.pcr.0（權限不足）——無法對帳，不下判決")))
            .ToList();

    private static HardwareFact Fact(string key, string value, FactAvailability availability = FactAvailability.Present,
        string? reason = null, double? numeric = null)
        => new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Reported, false, At, numeric, availability, reason);

    private static IReadOnlyList<HardwareFact> SampleFacts() =>
    [
        Fact("reg.microcode", "0x02007006", numeric: 0x02007006),
        Fact("msr.0x8b", "0x02007006", numeric: 0x02007006),
        Fact("tpm.pcr.0", "——", FactAvailability.InsufficientPrivilege, "需要提權"),
    ];

    private static IReadOnlyList<HardwareFact> Collect(List<FactRelationRow> rows, IReadOnlyList<HardwareFact>? facts = null,
        HardwareFact? chain = null)
        => FirmwareRiskFactsService.Collect(At, rows, facts ?? SampleFacts(), chain);

    private static HardwareFact One(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── P2-099 構成 ───────────────────────────────────────────────────────

    [Fact]
    public void 收集會產生五把鍵而且全部是可陳述的結論()
    {
        var facts = Collect(Rows(20, 1, 8));

        Assert.Equal([FirmwareRiskFactsService.PartsKey, FirmwareRiskFactsService.LevelKey,
            FirmwareRiskFactsService.GapKey, FirmwareRiskFactsService.BaselineKey,
            FirmwareRiskFactsService.ReportKey], facts.Select(f => f.Key));

        foreach (var f in facts)
        {
            Assert.Equal(FactAvailability.Present, f.Availability);
            // Present 的事實沒有「為什麼讀不到」可言（HardwareFact 的第二條不變式）
            Assert.Null(f.UnavailableReason);
            Assert.Equal(FactTrustLevel.Derived, f.Trust);
        }
    }

    [Fact]
    public void 構成列出每一條規則與其關係而且來源說得出規則集()
    {
        var rows = Rows(3, 1, 1);
        var parts = One(Collect(rows), FirmwareRiskFactsService.PartsKey);

        Assert.Contains("構成 5 條對帳規則", parts.Value);
        Assert.Contains("一致 3、矛盾 1、無法驗證 1", parts.Value);
        foreach (var r in rows) Assert.Contains($"{r.RuleId}＝", parts.Value);
        Assert.Contains("FactRelationRules", parts.Source);
        // 構成不得由本服務另立第二份清單——兩份清單遲早會不一樣
        Assert.Contains("不由本服務另立一份清單", parts.Value);
    }

    // ── P2-100 等級 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0, "無法評估")]
    [InlineData(5, 0, 5, "無法評估")]
    [InlineData(5, 1, 0, "有矛盾項")]
    [InlineData(5, 0, 1, "部分未定")]
    [InlineData(5, 0, 0, "全部一致")]
    public void 等級只由有沒有決定(int rules, int contradicts, int unverifiable, string expected)
        => Assert.Equal(expected, FirmwareRiskFactsService.LevelToken(rules, contradicts, unverifiable));

    [Fact]
    public void 等級的事實要寫出界線與政策版本()
    {
        var level = One(Collect(Rows(1, 1, 0)), FirmwareRiskFactsService.LevelKey);

        Assert.Contains("等級：有矛盾項", level.Value);
        Assert.Contains("沒有加權、沒有分數、沒有可調的門檻", level.Value);
        Assert.Contains("不是機器的安全狀態", level.Value);
        Assert.Contains($"政策 v{FirmwareRiskFactsService.PolicyVersion}", level.Value);
        // 等級是分類，不是數字：帶了數字等於邀請呼叫端拿它去算
        Assert.Null(level.NumericValue);
    }

    [Fact]
    public void 等級是四個允許值之一不得成為程度副詞()
    {
        string[] allowed = ["無法評估", "有矛盾項", "部分未定", "全部一致"];
        foreach (var rows in new[] { Rows(3, 0, 0), Rows(3, 1, 0), Rows(3, 0, 1), Rows(0, 0, 0) })
        {
            string level = One(Collect(rows), FirmwareRiskFactsService.LevelKey).Value;
            string token = level.Split('（')[0].Replace("等級：", "", StringComparison.Ordinal);
            Assert.Contains(token, allowed);
        }
    }

    // ── P2-101 缺口 ───────────────────────────────────────────────────────

    [Fact]
    public void 缺口列出每一條未取得的規則與原因()
    {
        var gap = One(Collect(Rows(2, 0, 2)), FirmwareRiskFactsService.GapKey);

        Assert.Contains("未取得 2 條", gap.Value);
        Assert.Contains("u0（輸入事實讀不到：tpm.pcr.0（權限不足）——無法對帳，不下判決）", gap.Value);
        Assert.Contains("不被當成 0 或「沒問題」", gap.Value);
        Assert.Equal(2, gap.NumericValue);
    }

    [Fact]
    public void 缺口為零時說沒有未取得項而不是沒問題()
    {
        var gap = One(Collect(Rows(3, 0, 0)), FirmwareRiskFactsService.GapKey);

        Assert.Contains("本輪沒有未取得項", gap.Value);
        Assert.Contains("缺項若出現會被列在這裡", gap.Value);
        // 「沒有缺口」不等於「沒有問題」——界線句必須還在
        Assert.Contains("缺的是「不知道」，不是「沒有」", gap.Value);
        Assert.Equal(0, gap.NumericValue);
    }

    // ── P2-102 基線 ───────────────────────────────────────────────────────

    [Fact]
    public void 基線的雜湊對同一份輸入穩定且格式是六十四位小寫十六進位()
    {
        var rows = Rows(3, 1, 1);
        string a = FirmwareRiskFactsService.BaselineJson(rows, SampleFacts());
        string b = FirmwareRiskFactsService.BaselineJson(Rows(3, 1, 1), SampleFacts());
        string hash = FirmwareRiskFactsService.ContentHash(a);

        Assert.Equal(a, b);
        Assert.Equal(hash, FirmwareRiskFactsService.ContentHash(b));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void 基線內容不含時間與機器識別()
    {
        string json = FirmwareRiskFactsService.BaselineJson(Rows(1, 0, 0), SampleFacts());

        Assert.DoesNotContain("2026-10-18", json);
        Assert.DoesNotContain("匿名", json);
        Assert.DoesNotContain(At.ToString("O"), json);
        Assert.Contains($"\"policy\": \"{FirmwareRiskFactsService.PolicyVersion}\"", json);
    }

    [Fact]
    public void 基線內容的規則與鍵都是排序過的()
    {
        string json = FirmwareRiskFactsService.BaselineJson(Rows(2, 1, 1), SampleFacts());
        using var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("rules").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
        var keys = doc.RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToList();

        Assert.Equal(rules.OrderBy(x => x, StringComparer.Ordinal), rules);
        Assert.Equal(keys.OrderBy(x => x, StringComparer.Ordinal), keys);
        Assert.Contains(FirmwareRiskFactsService.LevelKey, keys);   // 參與鍵含本組自己的六把
    }

    // ── P2-103 差異 ───────────────────────────────────────────────────────

    private static HardwareSnapshot Snapshot(string machineUuid, params HardwareFact[] facts)
        => HardwareSnapshotService.Create($"{AppInfo.Version}-test",
            new[] { Fact("system.uuid", machineUuid) }.Concat(facts), includeSensitive: false);

    [Fact]
    public void 差異列出參與鍵的變更並說明不建因果()
    {
        var before = Snapshot("MACHINE-AAAA-1111-2222", Fact("reg.microcode", "0x01", numeric: 1));
        var after = Snapshot("MACHINE-AAAA-1111-2222", Fact("reg.microcode", "0x02", numeric: 2));

        var delta = FirmwareRiskFactsService.Delta(before, after, At);

        Assert.Equal(FirmwareRiskFactsService.DeltaKey, delta.Key);
        Assert.Equal(FactAvailability.Present, delta.Availability);
        Assert.Contains("reg.microcode", delta.Value);
        Assert.Contains("不建因果", delta.Value);
        Assert.True(delta.NumericValue >= 1);
    }

    [Fact]
    public void 差異只比對參與風險陳述的鍵()
    {
        var before = Snapshot("MACHINE-AAAA-1111-2222", Fact("gpu.name", "A"));
        var after = Snapshot("MACHINE-AAAA-1111-2222", Fact("gpu.name", "B"));

        var delta = FirmwareRiskFactsService.Delta(before, after, At);

        Assert.DoesNotContain("gpu.name", delta.Value);
        Assert.Contains("沒有差異", delta.Value);
    }

    [Fact]
    public void 差異沒有差異時說沒有差異而不是沒有變更()
    {
        var before = Snapshot("MACHINE-AAAA-1111-2222", Fact("reg.microcode", "0x01"));
        var after = Snapshot("MACHINE-AAAA-1111-2222", Fact("reg.microcode", "0x01"));

        var delta = FirmwareRiskFactsService.Delta(before, after, At);

        Assert.Contains("沒有差異", delta.Value);
        Assert.Contains("沒有差異不等於沒有變更", delta.Value);
        Assert.Equal(0, delta.NumericValue);
    }

    [Fact]
    public void 差異跨機器要拒比且不帶數字()
    {
        var before = Snapshot("MACHINE-AAAA-1111-2222", Fact("reg.microcode", "0x01"));
        var after = Snapshot("MACHINE-BBBB-3333-4444", Fact("reg.microcode", "0x02"));

        var delta = FirmwareRiskFactsService.Delta(before, after, At);

        Assert.Equal(FactAvailability.NotApplicable, delta.Availability);
        Assert.Null(delta.NumericValue);                       // 不變式：非 Present 不得帶數字
        Assert.NotNull(delta.UnavailableReason);
        Assert.Contains("不同", delta.UnavailableReason!);
    }

    // ── P2-104 報告 ───────────────────────────────────────────────────────

    [Fact]
    public void 報告可以在沒有本程式的機器上離線核驗()
    {
        var rows = Collect(Rows(2, 1, 1)).Select(EvidenceFactRow.From).ToList();
        string html = FirmwareRiskFactsService.BuildThirdPartyReport(rows, At);

        Assert.True(HtmlReportService.Verify(html), "報告尾端應嵌對內容計算的雜湊，且可重算驗證");
        Assert.Contains("韌體風險陳述", html);
    }

    [Fact]
    public void 報告事實取不到審計鏈時不得說成沒問題()
    {
        var report = One(Collect(Rows(1, 0, 0)), FirmwareRiskFactsService.ReportKey);

        Assert.Contains("審計鏈狀態：未取得", report.Value);
        Assert.Contains("不以「沒有問題」代替", report.Value);
        Assert.Contains("P1-072", report.Value);
    }

    [Fact]
    public void 報告事實有審計鏈時引用它的值()
    {
        var chain = Fact(FirmwareRiskFactsService.ChainVerifyKey, "審計鏈與 manifest 互相印證", numeric: 2);
        var report = One(Collect(Rows(1, 0, 0), null, chain), FirmwareRiskFactsService.ReportKey);

        Assert.Contains("審計鏈與 manifest 互相印證", report.Value);
        Assert.DoesNotContain("審計鏈狀態：未取得", report.Value);
    }

    // ── 界線本身也要被守著 ────────────────────────────────────────────────

    /// <summary>
    /// 這一組的來源是**同一輪已經收集好的事實**——它不得自己新增任何讀取路徑。
    /// 這一條掃原始碼（不是掃執行期），因為「多了一個 P/Invoke」這種事不會有任何症狀。
    /// </summary>
    [Fact]
    public void 風險陳述不得自己新增讀取路徑()
    {
        string source = ReadRepoFile(Path.Combine("Services", "FirmwareRiskFactsService.cs"));
        string[] forbidden =
        [
            "DllImport", "ManagementObject", "Registry", "System.IO", "File.", "Directory.",
            "HttpClient", "TcpClient", "Socket", "Process.", "WmiQuery",
        ];
        foreach (string token in forbidden)
            Assert.DoesNotContain(token, source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }
}
