using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 端點政策批次（Vol2 推進第 84–93 項）的守門：全部注入假件——
/// 真實通路碰登錄與 WMI，單元測試讀真的會依機器而定。
/// 紅線：RDP 不匯出憑證內容；相機不開啟鏡頭；政策「不存在」與「讀不到」分開。
/// </summary>
public class EndpointPolicyFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        EndpointPolicyFactsService.ValueProbe? values = null,
        EndpointPolicyFactsService.KeyExistsProbe? keys = null,
        EndpointPolicyFactsService.PnpProbe? pnp = null,
        EndpointPolicyFactsService.CameraProbe? cameras = null)
        => EndpointPolicyFactsService.Collect(At, values, keys, pnp, cameras);

    // ── SE-006 RDP 憑證 ─────────────────────────────────────────────────

    [Fact]
    public void RDP_指定自訂憑證_如實且不匯出憑證內容()
    {
        var f = F(Collect(keys: (p, v) => p.Contains("RDP-Tcp")), EndpointPolicyFactsService.RdpCertKey);

        Assert.Equal(1, f.NumericValue);
        Assert.Contains("未匯出、未驗證鏈", f.Value);
        Assert.DoesNotContain("SHA1=", f.Value);
    }

    [Fact]
    public void RDP_自簽預設_講明客戶端會看到警告()
    {
        var f = F(Collect(keys: (p, v) => false), EndpointPolicyFactsService.RdpCertKey);
        Assert.Contains("自簽的預設 RDP 憑證", f.Value);
    }

    // ── SE-007 NTLM ─────────────────────────────────────────────────────

    [Fact]
    public void NTLM_政策值逐級對照_未設定不代為推定()
    {
        var lvl5 = F(Collect(values: (p, v) => v == "LmCompatibilityLevel" ? 5 : null), EndpointPolicyFactsService.NtlmKey);
        Assert.Contains("NTLMv2", lvl5.Value);
        Assert.Contains("企業建議值", lvl5.Value);

        var unset = F(Collect(values: (p, v) => null), EndpointPolicyFactsService.NtlmKey);
        Assert.Contains("本工具不代為推定", unset.Value);
        Assert.DoesNotContain("未收錄", unset.Value);
    }

    [Fact]
    public void NTLM_未收錄值_如實列出()
    {
        var f = F(Collect(values: (p, v) => 9), EndpointPolicyFactsService.NtlmKey);
        Assert.Contains("未收錄值 9", f.Value);
    }

    // ── SE-008 Kerberos ─────────────────────────────────────────────────

    [Fact]
    public void Kerberos_有政策覆寫_列出各項()
    {
        var f = F(Collect(values: (p, v) => v == "MaxTicketAge" ? 10 : (v == "MaxRenewAge" ? 168 : null)),
            EndpointPolicyFactsService.KerbKey);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("MaxTicketAge＝10", f.Value);
        Assert.Contains("KDC 決定", f.Value);
    }

    [Fact]
    public void Kerberos_無覆寫_0是值_標記鍵不存在()
    {
        var f = F(Collect(keys: (p, v) => false), EndpointPolicyFactsService.KerbKey);
        Assert.Contains("0 是值、不是缺席", f.Value);
        Assert.Contains("鍵不存在", f.Source);   // 來源欄記錄「鍵不存在」與「鍵存在但無覆寫」的差別
    }

    // ── SE-013 WinRE ────────────────────────────────────────────────────

    [Fact]
    public void WinRE_存在與版本_不觸發回復環境()
    {
        var f = F(Collect(keys: (p, v) => p.Contains("WinRE"), values: (p, v) => v == "WindowsREVersion" ? "10.0" : null),
            EndpointPolicyFactsService.WinreKey);

        Assert.Contains("版本 10.0", f.Value);
        Assert.Contains("不觸發", f.Value);
    }

    [Fact]
    public void WinRE_沒有鍵_講明_reagentc_才是權威()
    {
        var f = F(Collect(keys: (p, v) => false), EndpointPolicyFactsService.WinreKey);
        Assert.Contains("Reagentc /info 才是權威來源", f.Value);
    }

    // ── ZR-003 CBA ──────────────────────────────────────────────────────

    [Fact]
    public void CBA_政策強制智慧卡_不等於已生效()
    {
        var f = F(Collect(values: (p, v) => v == "AllowDomainSmartcardLogon" ? 1 : null,
            keys: (p, v) => p.Contains("CredentialsDelegation")), EndpointPolicyFactsService.CbaKey);

        Assert.Contains("不等於「CBA 已生效」", f.Value);
    }

    [Fact]
    public void CBA_未設定_0是值()
    {
        var f = F(Collect(values: (p, v) => null, keys: (p, v) => false), EndpointPolicyFactsService.CbaKey);
        Assert.Contains("0 是值、不是缺席", f.Value);
    }

    // ── ZR-006 動態鎖定 ─────────────────────────────────────────────────

    [Fact]
    public void 動態鎖定_啟用與停用分開_鎖定畫面狀態並列()
    {
        var enabled = F(Collect(values: (p, v) => v == "EnableGoodbye" ? 1 : null,
            keys: (p, v) => p.Contains("LockScreen")), EndpointPolicyFactsService.DynLockKey);
        Assert.Contains("政策為啟用", enabled.Value);
        Assert.Contains("未關閉", enabled.Value);

        var disabled = F(Collect(values: (p, v) => v == "EnableGoodbye" ? 0 : (v == "NoLockScreen" ? 1 : null),
            keys: (p, v) => p.Contains("LockScreen")), EndpointPolicyFactsService.DynLockKey);
        Assert.Contains("政策為停用", disabled.Value);
        Assert.Contains("NoLockScreen=1", disabled.Value);
    }

    [Fact]
    public void 動態鎖定_未政策化_預設啟用但不代硬體判斷()
    {
        var f = F(Collect(values: (p, v) => null, keys: (p, v) => false), EndpointPolicyFactsService.DynLockKey);
        Assert.Contains("預設啟用", f.Value);
        Assert.Contains("硬體是否可用另計", f.Value);
    }

    // ── HM-003 資源衝突 ─────────────────────────────────────────────────

    [Fact]
    public void 資源衝突_只認12_24_31_其他碼不誤報()
    {
        var f = F(Collect(pnp: () => new List<(string, uint, bool)>
        {
            ("裝置A", 12, true), ("裝置B", 28, true), ("裝置C", 24, true), ("幽靈", 45, false),
        }), EndpointPolicyFactsService.ConflictKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("代碼 12", f.Value);
        Assert.Contains("代碼 24", f.Value);
        Assert.DoesNotContain("代碼 28", f.Value);
        Assert.DoesNotContain("幽靈", f.Value);
    }

    [Fact]
    public void 資源衝突_0條_0是值不是缺席()
    {
        var f = F(Collect(pnp: () => new List<(string, uint, bool)> { ("正常裝置", 0, true) }),
            EndpointPolicyFactsService.ConflictKey);
        Assert.Contains("0 是值、不是缺席", f.Value);
    }

    // ── MU-004 相機 ─────────────────────────────────────────────────────

    [Fact]
    public void 相機_只數裝置_紅線寫在值裡()
    {
        var f = F(Collect(cameras: () => new List<(string, bool)> { ("Cam1", true), ("Cam2", true), ("Cam3", false) }),
            EndpointPolicyFactsService.CameraKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("不開啟鏡頭", f.Value);
    }

    [Fact]
    public void 相機_列舉失敗_ReadError()
    {
        var f = F(Collect(cameras: () => throw new IOException("x")), EndpointPolicyFactsService.CameraKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── UF-004 就緒度 ───────────────────────────────────────────────────

    [Fact]
    public void 就緒度_唯讀評估_不宣稱可以升()
    {
        var f = F(Collect(values: (p, v) => v == "CurrentBuildNumber" ? "26100" : null,
            keys: (p, v) => p.Contains("BIOS")), EndpointPolicyFactsService.ReadyKey);

        Assert.Contains("26100", f.Value);
        Assert.Contains("不宣稱「可以升」", f.Value);
    }

    // ── ZR-008 合規報告 ─────────────────────────────────────────────────

    [Fact]
    public void 合規報告_明講不做判決()
    {
        var f = F(Collect(), EndpointPolicyFactsService.ComplianceReportKey);
        Assert.Contains("不做合規判決", f.Value);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 整組_非Present都帶原因()
    {
        var facts = Collect(values: (p, v) => throw new IOException("x"), keys: (p, v) => throw new IOException("x"),
            pnp: () => throw new IOException("x"), cameras: () => throw new IOException("x"));
        foreach (var f in facts.Where(f => f.Availability != FactAvailability.Present))
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺三態原因");
    }

    [Fact]
    public void 整組_正常通路_10把鍵齊()
    {
        var facts = Collect(values: (p, v) => v == "CurrentBuildNumber" ? "26100" : null,
            keys: (p, v) => false,
            pnp: () => new List<(string, uint, bool)> { ("ok", 0, true) },
            cameras: () => new List<(string, bool)> { ("cam", true) });

        string[] expected =
        [
            EndpointPolicyFactsService.RdpCertKey, EndpointPolicyFactsService.NtlmKey,
            EndpointPolicyFactsService.KerbKey, EndpointPolicyFactsService.WinreKey,
            EndpointPolicyFactsService.CbaKey, EndpointPolicyFactsService.DynLockKey,
            EndpointPolicyFactsService.ConflictKey, EndpointPolicyFactsService.CameraKey,
            EndpointPolicyFactsService.ReadyKey, EndpointPolicyFactsService.ComplianceReportKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
    }
}
