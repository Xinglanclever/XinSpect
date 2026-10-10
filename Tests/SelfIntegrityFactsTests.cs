using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 自我完整性事實組（IN-001/002/004/007/010）的守門。
/// <para>
/// 全部走注入假件：這一組碰的是執行檔本身、設定檔、token、已載入模組——在單元測試裡讀真的那些，
/// 結果會取決於跑測試的機器；而<b>寫真的</b>審計日誌更是測試不該做的事（只有一條往返測試
/// 寫進暫存目錄）。
/// </para>
/// </summary>
public class SelfIntegrityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);

    // ── 假件 ──────────────────────────────────────────────────────────────

    private static AuditEntry Baseline(string scope, string hash) =>
        AuditLogService.Append([], "tester", "machine", "自我完整性基線", scope, "測試基線", hash, At);

    private static SelfIntegrityInputs Inputs(
        string? selfHash = "aaaa1111bbbb2222cccc3333dddd4444",
        string? configText = "{\"k\":1}",
        string? configHash = "55556666777788889999000011112222",
        IReadOnlyList<AuditEntry>? audit = null,
        IReadOnlyList<string>? modules = null,
        Func<string, (bool? Ok, string Note)>? verify = null,
        TokenPrivilegeSnapshot? privs = null,
        bool auditUnreadable = false) => new(
        BinaryPath: @"C:\App\XinSpect.exe",
        HashFile: path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? selfHash : configHash,
        SizeOf: _ => 31_315_243,
        ConfigPath: @"C:\Data\settings.json",
        ReadText: _ => configText,
        // 預設一個系統目錄模組：讓「依賴完整性」在預設情境是 Present，
        // 需要「列舉失敗」的測試再自己傳空清單。
        LoadedModulePaths: () => modules ?? [@"C:\Windows\System32\ntdll.dll"],
        VerifySignature: verify ?? (_ => (true, "")),
        LoadAudit: () => auditUnreadable ? null : (audit ?? []),
        ReadPrivileges: () => privs);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(SelfIntegrityInputs inputs) =>
        SelfIntegrityFactsService.Collect(At, inputs);

    // ── IN-001 自身二進位自檢 ─────────────────────────────────────────────

    [Fact]
    public void 自身二進位_與審計基線相符()
    {
        var inputs = Inputs(audit: [Baseline(SelfIntegrityFactsService.SelfAuditScope, "aaaa1111bbbb2222cccc3333dddd4444")]);
        var f = Fact(Collect(inputs), SelfIntegrityFactsService.SelfKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("與審計基線相符", f.Value);
        Assert.Contains("SHA-256", f.Value);
    }

    [Fact]
    public void 自身二進位_與基線不符要說出不符與基線值()
    {
        var inputs = Inputs(audit: [Baseline(SelfIntegrityFactsService.SelfAuditScope, "9999888877776666")]);
        var f = Fact(Collect(inputs), SelfIntegrityFactsService.SelfKey);

        Assert.Contains("與審計基線不符", f.Value);
        Assert.Contains("9999888877776666", f.Value);
    }

    [Fact]
    public void 沒有基線時_如實說還沒有基線而不是相符()
    {
        var f = Fact(Collect(Inputs(audit: [])), SelfIntegrityFactsService.SelfKey);

        Assert.Contains("還沒有這條基線", f.Value);
        Assert.DoesNotContain("相符", f.Value);
    }

    [Fact]
    public void 審計讀不到時_不說相符也不說沒有基線()
    {
        var inputs = Inputs(auditUnreadable: true);
        var f = Fact(Collect(inputs), SelfIntegrityFactsService.SelfKey);

        Assert.Contains("無法對帳", f.Value);
        Assert.DoesNotContain("與審計基線相符", f.Value);
        Assert.DoesNotContain("還沒有這條基線", f.Value);
    }

    [Fact]
    public void 自身二進位讀不到_標讀取錯誤且不給值()
    {
        var f = Fact(Collect(Inputs(selfHash: null)), SelfIntegrityFactsService.SelfKey);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Equal("", f.Value);
        Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason));
    }

    [Fact]
    public void 每個自身二進位事實都要帶防篡改界線()
    {
        var f = Fact(Collect(Inputs(audit: [])), SelfIntegrityFactsService.SelfKey);

        Assert.Contains("不是防篡改保證", f.Value);
    }

    // ── IN-002 設定檔反篡改 ──────────────────────────────────────────────

    [Fact]
    public void 設定檔損毀_標Unknown並要求先備份()
    {
        var f = Fact(Collect(Inputs(configText: "{ 這不是 JSON")), SelfIntegrityFactsService.ConfigKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("損毀", f.Value);
        Assert.Contains("備份", f.Value);
    }

    [Fact]
    public void 設定檔不存在_標不適用而不是錯誤()
    {
        var f = Fact(Collect(Inputs(configText: null)), SelfIntegrityFactsService.ConfigKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用", f.UnavailableReason);
    }

    [Fact]
    public void 設定檔_與審計基線相符與不符都說得出()
    {
        var same = Fact(Collect(Inputs(audit: [Baseline(SelfIntegrityFactsService.ConfigAuditScope, "55556666777788889999000011112222")])),
            SelfIntegrityFactsService.ConfigKey);
        var differ = Fact(Collect(Inputs(audit: [Baseline(SelfIntegrityFactsService.ConfigAuditScope, "deadbeefdeadbeef")])),
            SelfIntegrityFactsService.ConfigKey);

        Assert.Contains("與審計基線相符", same.Value);
        Assert.Contains("與審計基線不符", differ.Value);
    }

    // ── IN-004 token 特權 ────────────────────────────────────────────────

    [Fact]
    public void 有權限快照_要說出提權與特權數()
    {
        var privs = new TokenPrivilegeSnapshot(true, "高", 24, ["SeDebugPrivilege", "SeShutdownPrivilege"]);
        var f = Fact(Collect(Inputs(privs: privs)), SelfIntegrityFactsService.PrivsKey);

        Assert.Contains("提權：是", f.Value);
        Assert.Contains("完整性層級 高", f.Value);
        Assert.Contains("24", f.Value);
        Assert.Contains("SeDebugPrivilege", f.Value);
    }

    [Fact]
    public void 權限讀不到_標讀取錯誤不冒充未提權()
    {
        var f = Fact(Collect(Inputs(privs: null)), SelfIntegrityFactsService.PrivsKey);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Equal("", f.Value);
    }

    // ── IN-007 依賴完整性 ────────────────────────────────────────────────

    [Fact]
    public void 模組_乾淨的不列名_未簽章與側載列名()
    {
        string temp = Path.GetTempPath();
        var modules = new List<string>
        {
            @"C:\Windows\System32\ntdll.dll",          // 系統目錄：不驗、不列名
            @"C:\Program Files\App\clean.dll",         // 非系統但已簽章：不列名
            @"C:\Program Files\App\unsigned.dll",      // 非系統且未簽章：列名
            Path.Combine(temp, "hooked.dll"),          // 側載候選：列名
        };
        var verify = (string p) => p.EndsWith("unsigned.dll", StringComparison.OrdinalIgnoreCase)
            ? ((bool?)false, "0x800B0100")
            : ((bool?)true, "");
        var facts = Collect(Inputs(modules: modules, verify: verify));

        var summary = Fact(facts, SelfIntegrityFactsService.DepsKey);
        Assert.Contains("模組 4 個", summary.Value);
        Assert.Contains("非系統目錄 3 個", summary.Value);
        Assert.Contains("未通過簽章 1 個", summary.Value);
        Assert.Contains("側載候選 1 個", summary.Value);
        Assert.Contains("unsigned.dll", summary.Value);
        Assert.Contains("0x800B0100", summary.Value);
        Assert.Contains("hooked.dll", summary.Value);
        Assert.DoesNotContain("clean.dll", summary.Value);

        // 逐項明細在值裡（不另立鍵）：這一組只產一個鍵
        Assert.DoesNotContain(facts, f => f.Key.StartsWith("in.deps.", StringComparison.Ordinal));
    }

    [Fact]
    public void 模組簽章無法驗證_如實寫無法驗證而不是未通過()
    {
        var facts = Collect(Inputs(
            modules: [@"C:\Tools\thing.dll"],
            verify: _ => (null, "檔案不存在——無法驗證")));

        var summary = Fact(facts, SelfIntegrityFactsService.DepsKey);
        Assert.Contains("thing.dll（簽章無法驗證", summary.Value);
        Assert.DoesNotContain("thing.dll（未通過簽章", summary.Value);
    }

    [Fact]
    public void 模組列舉失敗_標讀取錯誤()
    {
        var f = Fact(Collect(Inputs(modules: [])), SelfIntegrityFactsService.DepsKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\driver.dll", false)]
    [InlineData(@"C:\Program Files\App\lib.dll", false)]
    [InlineData(@"C:\Program Files (x86)\App\lib.dll", false)]
    public void 側載候選判定_系統與程式目錄不算(string path, bool expected)
    {
        Assert.Equal(expected, SelfIntegrityFactsService.IsSideloadCandidate(path));
    }

    [Fact]
    public void 側載候選判定_暫存目錄算()
    {
        Assert.True(SelfIntegrityFactsService.IsSideloadCandidate(Path.Combine(Path.GetTempPath(), "a.dll")));
    }

    // ── IN-010 報告 ──────────────────────────────────────────────────────

    [Fact]
    public void 報告_有待確認項時不得讀成全部通過()
    {
        var facts = Collect(Inputs(selfHash: null, privs: null));   // 兩項讀不到
        var report = Fact(facts, SelfIntegrityFactsService.ReportKey);

        Assert.Equal(FactAvailability.Unknown, report.Availability);
        Assert.Contains("待確認 2 項", report.Value);
        Assert.Contains("不讀成全部通過", report.UnavailableReason ?? "");
    }

    [Fact]
    public void 報告_全可採信時標Present_但仍聲明沒有防篡改保證()
    {
        var facts = Collect(Inputs(audit: [], privs: new TokenPrivilegeSnapshot(false, "中", 3, [])));
        var report = Fact(facts, SelfIntegrityFactsService.ReportKey);

        Assert.Equal(FactAvailability.Present, report.Availability);
        Assert.Contains("可採信 4 項、待確認 0 項", report.Value);
        Assert.Contains("沒有防篡改保證", report.Value);
    }

    // ── 真實來源的煙霧測試與基線往返（暫存目錄）──────────────────────────

    [Fact]
    public void 真實來源收集_不拋例外且五個鍵都在()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinself-" + Guid.NewGuid().ToString("N"));
        try
        {
            var facts = Collect(SelfIntegrityInputs.Real(
                configPath: Path.Combine(dir, "settings.json"), auditPath: Path.Combine(dir, "audit.json")));
            var keys = facts.Select(f => f.Key).ToHashSet();

            Assert.Contains(SelfIntegrityFactsService.SelfKey, keys);
            Assert.Contains(SelfIntegrityFactsService.ConfigKey, keys);
            Assert.Contains(SelfIntegrityFactsService.PrivsKey, keys);
            Assert.Contains(SelfIntegrityFactsService.DepsKey, keys);
            Assert.Contains(SelfIntegrityFactsService.ReportKey, keys);
            // 逐項模組旗標可有可無（取決於測試主機載入了什麼），但不得出現 in. 之外的鍵
            Assert.All(keys, k => Assert.StartsWith("in.", k));
            Assert.True(facts.Count >= 5);

            // 報告必定是這一組的最後一列，且不得因為「有待確認項」而消失
            Assert.Equal(SelfIntegrityFactsService.ReportKey, facts[^1].Key);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void 記錄基線後_自身二進位與設定檔都對得上()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinself-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string config = Path.Combine(dir, "settings.json");
        string audit = Path.Combine(dir, "audit.json");
        File.WriteAllText(config, "{\"updateIntervalSec\":1}");
        try
        {
            var inputs = SelfIntegrityInputs.Real(config, audit);

            // 未記錄前：如實說沒有基線
            Assert.Contains("還沒有這條基線", Fact(Collect(inputs), SelfIntegrityFactsService.SelfKey).Value);

            var (written, summary, failure) = SelfIntegrityFactsService.RecordBaseline(inputs, audit);
            Assert.True(written, failure ?? "應寫入成功");
            Assert.Contains("鏈雜湊可驗", summary);

            var after = Collect(SelfIntegrityInputs.Real(config, audit));
            Assert.Contains("與審計基線相符", Fact(after, SelfIntegrityFactsService.SelfKey).Value);
            Assert.Contains("與審計基線相符", Fact(after, SelfIntegrityFactsService.ConfigKey).Value);

            // 設定檔改一個字之後，對帳必須變成不符（而不是繼續說相符）
            File.WriteAllText(config, "{\"updateIntervalSec\":2}");
            Assert.Contains("與審計基線不符",
                Fact(Collect(SelfIntegrityInputs.Real(config, audit)), SelfIntegrityFactsService.ConfigKey).Value);

            // 寫進去的審計日誌本身要是條完整的鏈
            Assert.True(AuditVerifier.VerifyFile(audit).Valid);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void 記錄基線失敗時_不得宣稱已建立()
    {
        // 指向一個不可能寫入的路徑（把檔案當目錄用）
        string dir = Path.Combine(Path.GetTempPath(), "xinself-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "x");
        try
        {
            var (written, _, failure) = SelfIntegrityFactsService.RecordBaseline(
                SelfIntegrityInputs.Real(Path.Combine(dir, "settings.json"), Path.Combine(blocker, "audit.json")),
                Path.Combine(blocker, "audit.json"));

            Assert.False(written);
            Assert.Contains("基線沒建立", failure ?? "");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
