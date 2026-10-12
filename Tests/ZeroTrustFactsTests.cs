using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 推進第 67–74 項（零信任／資料安全／查詢補完）的守門：全部注入假件——
/// 真實通路碰 WMI／登錄／X509Store／auditpol 行程，單元測試讀真的會依機器而定。
/// </summary>
public class ZeroTrustFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        TrustOpsFactsService.PipeProbe? pipes = null,
        TrustOpsFactsService.AuditProbe? audit = null,
        ZeroTrustFactsService.MdmProbe? mdm = null,
        ZeroTrustFactsService.RegistryFlagProbe? flags = null,
        ZeroTrustFactsService.CertProbe? certs = null,
        bool cmdlineOptedIn = false,
        ZeroTrustFactsService.CommandRunner? runner = null,
        ZeroTrustFactsService.CmdlineProbe? cmdlines = null,
        ZeroTrustFactsService.TempProbe? temps = null)
        => ZeroTrustFactsService.Collect(At, mdm, flags, certs, runner, cmdlines, temps, cmdlineOptedIn);

    // ── ZR-001 MDM ──────────────────────────────────────────────────────

    [Fact]
    public void MDM_已註冊_報筆數與提供者()
    {
        var facts = Collect(mdm: () => new List<ZeroTrustFactsService.MdmEnrollment>
        {
            new("MS DM Server", 3), new("MS DM Server", 1), new("Other", 3),
        });
        var f = F(facts, ZeroTrustFactsService.MdmKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("MS DM Server×2", f.Value);
        Assert.Contains("Other×1", f.Value);
        Assert.Contains("UPN 不進值", f.Value);
    }

    [Fact]
    public void MDM_讀不到_ReadError_不假裝未註冊()
    {
        var f = F(Collect(mdm: () => null), ZeroTrustFactsService.MdmKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不假裝「未註冊」", f.UnavailableReason);
    }

    [Fact]
    public void MDM_沒有條目_0是值()
    {
        var f = F(Collect(mdm: () => []), ZeroTrustFactsService.MdmKey);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("沒有註冊條目", f.Value);
    }

    // ── ZR-002 Entra ────────────────────────────────────────────────────

    [Fact]
    public void Entra_已加入_如實陳述()
    {
        var f = F(Collect(flags: (path, value) => path.Contains("AAD")), ZeroTrustFactsService.AadjKey);
        Assert.Contains("已加入", f.Value);
        Assert.Contains("UPN 等識別資訊刻意不讀", f.Value);
    }

    [Fact]
    public void Entra_未加入無Workplace_講明是本機環境事實()
    {
        var f = F(Collect(flags: (path, value) => false), ZeroTrustFactsService.AadjKey);
        Assert.Contains("未加入 Entra", f.Value);
    }

    [Fact]
    public void Entra_讀不到_標ReadError()
    {
        var f = F(Collect(flags: (path, value) => throw new IOException("x")), ZeroTrustFactsService.AadjKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── ZR-007 遠端抹除能力 ─────────────────────────────────────────────

    [Fact]
    public void 抹除_MDM在_能力在管理服務_本工具永不觸發()
    {
        var facts = Collect(mdm: () => [new ZeroTrustFactsService.MdmEnrollment("X", 3)]);
        Assert.Contains("本工具永不觸發抹除", F(facts, ZeroTrustFactsService.WipeKey).Value);
    }

    [Fact]
    public void 抹除_無MDM但FindMyDevice啟用_說清楚通道()
    {
        var f = F(Collect(mdm: () => [], flags: (path, value) => path.Contains("FindMyDevice")),
            ZeroTrustFactsService.WipeKey);
        Assert.Contains("Find My Device 已啟用", f.Value);
    }

    [Fact]
    public void 抹除_兩者皆無_直說沒有通道()
    {
        var f = F(Collect(mdm: () => [], flags: (path, value) => false), ZeroTrustFactsService.WipeKey);
        Assert.Contains("沒有雲端遠端抹除通道", f.Value);
    }

    // ── DS-005 憑證時間線 ───────────────────────────────────────────────

    [Fact]
    public void 憑證時間線_到期與新增分窗_不列主體名稱()
    {
        var now = At.LocalDateTime;
        var facts = Collect(certs: () => new List<ZeroTrustFactsService.CertEntry>
        {
            new("LM/Root", now.AddDays(-400), now.AddDays(+30)),  // 90 天內到期（核發已久，不算新增）
            new("LM/CA", now.AddDays(-200), now.AddDays(+200)),   // 還久
            new("CU/My", now.AddDays(-95), now.AddDays(-5)),      // 已過期
            new("LM/Root", now.AddDays(-10), now.AddDays(+300)),  // 90 天內新增
        });
        var summary = F(facts, ZeroTrustFactsService.CertSummaryKey);

        Assert.Contains("90 天內到期 1", summary.Value);
        Assert.Contains("已過期 1", summary.Value);
        Assert.Contains("90 天內新增 1", summary.Value);
        Assert.DoesNotContain("CN=", summary.Value);
    }

    [Fact]
    public void 憑證時間線_列舉失敗_ReadError()
    {
        Assert.Equal(FactAvailability.ReadError, F(Collect(certs: () => null), ZeroTrustFactsService.CertCountKey).Availability);
    }

    // ── SE-005 auditpol 原樣輸出 ────────────────────────────────────────

    [Fact]
    public void 稽核_原樣輸出_不解析在地化文字()
    {
        var facts = Collect(runner: _ => "Auditpol output line A\r\nline B");
        var f = F(facts, ZeroTrustFactsService.Audit2Key);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("Auditpol output line A", f.Value);
        Assert.Contains("不解析在地化文字", f.Value);
    }

    [Fact]
    public void 稽核_命令失敗_ReadError()
    {
        Assert.Equal(FactAvailability.ReadError, F(Collect(runner: _ => null), ZeroTrustFactsService.Audit2Key).Availability);
    }

    // ── DL-005 行程命令列（L2）──────────────────────────────────────────

    [Fact]
    public void 命令列_未同意_不讀且值為空()
    {
        var f = F(Collect(cmdlineOptedIn: false), ZeroTrustFactsService.CmdSummaryKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Equal("", f.Value);
        Assert.Contains("預設不讀", f.UnavailableReason);
    }

    [Fact]
    public void 命令列_已同意_值標敏感交遮蔽()
    {
        var facts = Collect(cmdlineOptedIn: true,
            cmdlines: () => new List<(string, string)> { ("proc1", "C:\\app.exe -v"), ("proc2", "(讀不到)") });
        var summary = F(facts, ZeroTrustFactsService.CmdSummaryKey);

        Assert.Equal(2, F(facts, ZeroTrustFactsService.CmdCountKey).NumericValue);
        Assert.True(summary.Sensitive, "命令列可能含路徑與參數（L2），必須交遮蔽政策");
    }

    // ── HM-005 溫度交叉 ─────────────────────────────────────────────────

    [Fact]
    public void 溫度交叉_兩點以上_報跨幅且不下判決()
    {
        var facts = Collect(temps: () => new List<(string, double)>
        {
            ("ZoneA", 45.2), ("ZoneB", 52.0), ("ZoneC", 44.0),
        });
        var f = F(facts, ZeroTrustFactsService.SensDriftKey);

        Assert.Equal(8.0, f.NumericValue!.Value, precision: 1);
        Assert.Contains("ZoneB 52.0", f.Value);
        Assert.Contains("不是判決", f.Value);
    }

    [Fact]
    public void 溫度交叉_單一感測點_標不適用不是錯誤()
    {
        var f = F(Collect(temps: () => new List<(string, double)> { ("ZoneA", 45.0) }), ZeroTrustFactsService.SensDriftKey);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 溫度交叉_讀不到_ReadError()
    {
        Assert.Equal(FactAvailability.ReadError, F(Collect(temps: () => null), ZeroTrustFactsService.SensDriftKey).Availability);
    }

    // ── MU-008 HDR ──────────────────────────────────────────────────────

    [Fact]
    public void HDR_啟用與未啟用都如實()
    {
        Assert.Contains("已啟用", F(Collect(flags: (path, value) => path.Contains("VideoSettings")), ZeroTrustFactsService.HdrKey).Value);
        Assert.Contains("未啟用", F(Collect(flags: (path, value) => false), ZeroTrustFactsService.HdrKey).Value);
    }

    [Fact]
    public void HDR_讀不到_標NotSupported不推測()
    {
        var f = F(Collect(flags: (path, value) => throw new IOException("x")), ZeroTrustFactsService.HdrKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不推測", f.UnavailableReason);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 整組_非Present都帶原因()
    {
        var facts = Collect(mdm: () => null, flags: (path, value) => throw new IOException("x"),
            certs: () => null, runner: _ => null, temps: () => null, cmdlineOptedIn: false);
        foreach (var f in facts.Where(f => f.Availability != FactAvailability.Present))
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺三態原因");
    }
}
