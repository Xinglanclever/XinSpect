using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 推測層的三重標註契約：鍵名前綴、值前綴、信任等級必須同時存在；
/// 而且推測不得被合規判定或 Merkle 指紋當成證據。
/// </summary>
public class SpeculationFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 3, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string reason = "")
        => new(key, "測試域", key, value, "", "測試來源", FactTrustLevel.Measured, false, At,
            numeric, availability, availability == FactAvailability.Present ? null : reason);

    private static HardwareFact Get(System.Collections.Generic.IReadOnlyList<HardwareFact> facts, string key)
        => Assert.Single(facts, f => f.Key == key);

    [Fact]
    public void 七項推測_全部三重標註且沒有把猜測寫成讀取失敗()
    {
        var facts = SpeculationFactsService.Collect([], At);

        Assert.Equal(7, facts.Count);
        Assert.Equal(7, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var f in facts)
        {
            Assert.StartsWith("infer.", f.Key, StringComparison.Ordinal);
            Assert.StartsWith(SpeculationFactsService.Label, f.Value, StringComparison.Ordinal);
            Assert.Equal(FactTrustLevel.Speculative, f.Trust);
            Assert.Equal(FactAvailability.Present, f.Availability);
            Assert.Null(f.UnavailableReason);
            Assert.Equal("推測（非量測）", HardwareSnapshotService.TrustText(f.Trust));
            Assert.Contains("信心：", f.Value, StringComparison.Ordinal);
            Assert.Contains("依據：", f.Value, StringComparison.Ordinal);
            Assert.Contains("限制：", f.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 推測層的範圍聲明_說明白己不是合規證據()
    {
        var f = Get(SpeculationFactsService.Collect([], At), SpeculationFactsService.DisclaimerKey);

        Assert.Contains("非量測", f.Value, StringComparison.Ordinal);
        Assert.Contains("非合規證據", f.Value, StringComparison.Ordinal);
        Assert.Contains("為防推測被升格為證據", f.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void BootGuard目錄有IBB與ACM_只能說映像含元件而不能宣稱已啟用()
    {
        var source = new[]
        {
            Fact("fw.fit.summary", "FIT 條目 5 條"),
            Fact("bt.ibb", "啟動模組 1 條：位移 0x1000"),
            Fact("bt.manifests", "ACM 1、Boot Policy 1、啟動模組 1"),
            Fact("bt.km", "", availability: FactAvailability.NotSupported, reason: "型別編號無出處"),
            Fact("fw.bg.profile", "", availability: FactAvailability.NotSupported, reason: "未對準"),
        };

        var items = SpeculationFactsService.Evaluate(source, At);
        var item = Assert.Single(items, x => x.Fact.Key == SpeculationFactsService.BootGuardKey);

        Assert.Contains("包含 Boot Guard 元件", item.Fact.Value, StringComparison.Ordinal);
        Assert.Contains("不代表硬體熔絲", item.Fact.Value, StringComparison.Ordinal);
        Assert.Equal("低", item.Confidence);
        Assert.Contains("靜態映像內容", item.Limitations, StringComparison.Ordinal);
        Assert.Contains("不能說", item.Limitations, StringComparison.Ordinal);
        Assert.Contains("未驗", item.Limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void CXL有固定記憶體窗口_只能推測韌體已配置窗口而不能宣稱端點在線()
    {
        var source = new[]
        {
            Fact("cxl.cfmws.count", "2 個", 2),
            Fact("cxl.cfmws.0", "基底 0x100000000・大小 64 GiB"),
            Fact("ac.cedt", "CEDT 存在"),
        };

        var f = Get(SpeculationFactsService.Collect(source, At), SpeculationFactsService.CxlRoleKey);

        Assert.Contains("CXL 固定記憶體窗口", f.Value, StringComparison.Ordinal);
        Assert.Contains("已保留主機端實體位址空間", f.Value, StringComparison.Ordinal);
        Assert.Contains("不代表插槽上實際有已訓練成功", f.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("可能接有 Type 3", f.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TPM2背書階層可用_只說階層未鎖定且不宣稱Quote會成功()
    {
        var source = new[]
        {
            Fact("tpm.present", "可用（Windows TBS 仲介，唯讀查詢）"),
            Fact("tpm.version", "TPM 2.0"),
            Fact("tpm.attest.capable", "family 2.0；背書階層啟用＝是"),
            Fact("tpm.pcr.banks", "SHA-256", 1),
            Fact("tpm.owned", "已擁有＝是"),
        };

        var item = Assert.Single(SpeculationFactsService.Evaluate(source, At),
            x => x.Fact.Key == SpeculationFactsService.TpmQuoteKey);

        Assert.Contains("背書階層處於啟用狀態", item.Fact.Value, StringComparison.Ordinal);
        Assert.Contains("EK／AK", item.Fact.Value, StringComparison.Ordinal);
        Assert.Contains("實際 Quote 仍可能失敗", item.Fact.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("具備產生 TPM Quote 的本地條件", item.Fact.Value, StringComparison.Ordinal);
        Assert.Contains("沒有送出 challenge", item.Limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void 韌體年份只當極低信心線索_不冒充PQC能力證據()
    {
        var source = new[] { Fact("fw.date", "2026-01-15（原始 20260115000000.000000+000）") };

        var f = Get(SpeculationFactsService.Collect(source, At), SpeculationFactsService.UefiPqcKey);

        Assert.Contains("2026", f.Value, StringComparison.Ordinal);
        Assert.Contains("極低", f.Value, StringComparison.Ordinal);
        Assert.Contains("年份不是能力證明", f.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 未開啟不得被讀成開啟_更新驗證推測要落在關閉分支()
    {
        var source = new[]
        {
            Fact("uefi.secure_boot", "未開啟"),
            Fact("platform.secure_boot", "未開啟"),
        };

        var f = Get(SpeculationFactsService.Collect(source, At), SpeculationFactsService.UpdateAuthKey);

        Assert.Contains("回報關閉", f.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("可能走 UEFI", f.Value, StringComparison.Ordinal);
        Assert.Contains("不等於", f.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 推測不進Merkle指紋也不被合規判定當成通過證據()
    {
        var source = new[]
        {
            Fact("infer.fw.update_auth", "【推測】Secure Boot 開啟，更新可能驗簽"),
            Fact("infer.bt.boot_guard", "【推測】可能已佈署 Boot Guard"),
            Fact("infer.tpm.quote", "【推測】具備 Quote 前提"),
        };

        var tree = FactMerkleService.Build(source);
        Assert.Equal(0, tree.LeafCount);
        Assert.Null(tree.RootSha256);
        Assert.Equal(3, tree.Excluded.NotAllowlisted);

        var report = ComplianceMappingService.Evaluate(source);
        Assert.DoesNotContain(report.Controls, c => c.Status == ComplianceStatus.Satisfied);
        Assert.DoesNotContain(report.Techniques, t => t.Exposure == AttackExposure.Mitigated);
    }

    [Fact]
    public void 推測模式的CLI輸出_每一項都帶Speculative與推測前綴()
    {
        var stdout = new StringWriter();
        var source = new[]
        {
            Fact("uefi.secure_boot", "是", 1),
            Fact("tpm.present", "可用"),
        };

        int exit = CliService.Run(["--speculation"], () => source, stdout, new StringWriter());

        Assert.Equal(CliService.ExitOk, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.Equal("speculation", root.GetProperty("scope").GetString());
        Assert.Equal(SpeculationFactsService.Label, root.GetProperty("label").GetString());
        Assert.Equal("Speculative", root.GetProperty("defaultTrust").GetString());
        Assert.Equal(7, root.GetProperty("count").GetInt32());
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            Assert.StartsWith("infer.", item.GetProperty("key").GetString(), StringComparison.Ordinal);
            Assert.StartsWith(SpeculationFactsService.Label, item.GetProperty("value").GetString(),
                StringComparison.Ordinal);
            Assert.Equal("Speculative", item.GetProperty("trust").GetString());
            Assert.True(item.GetProperty("confidence").GetString()!.Length > 0);
            Assert.True(item.GetProperty("basisKeys").GetArrayLength() >= 0);
        }
    }
}