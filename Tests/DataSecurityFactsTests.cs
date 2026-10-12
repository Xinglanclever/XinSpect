using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 推進第 75–83 項（資料安全深化批次）的守門：全部注入假件——
/// 真實通路碰憑證店／登錄，單元測試讀真的會依機器而定。
/// 紅線：生物特徵永不讀（只數註冊項）；憑證只取時間與 store。
/// </summary>
public class DataSecurityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 11, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        DataSecurityFactsService.CertProbe? certs = null,
        DataSecurityFactsService.HelloProbe? hello = null,
        DataSecurityFactsService.FlagProbe? flags = null,
        DataSecurityFactsService.IntProbe? intFlags = null,
        DataSecurityFactsService.FileExistsProbe? fileExists = null,
        DataSecurityFactsService.PolicyProbe? policies = null)
        => DataSecurityFactsService.Collect(At, certs, hello, flags, fileExists, intFlags, policies);

    private static DataSecurityFactsService.CertInfo Cert(string store, bool self) =>
        new(store, At.LocalDateTime.AddDays(30), self);

    // ── DS-003 EFS ──────────────────────────────────────────────────────

    [Fact]
    public void EFS_有憑證_計數如實_不列主體()
    {
        var facts = Collect(certs: () => new List<DataSecurityFactsService.CertInfo>
        {
            Cert("CurrentUser/EFS 1.0", false), Cert("CurrentUser/.FileEncryption", false), Cert("LM/Root", false),
        });
        var f = F(facts, DataSecurityFactsService.EfsKey);

        Assert.Equal(2, f.NumericValue);
        Assert.DoesNotContain("CN=", f.Value);
    }

    [Fact]
    public void EFS_讀不到_ReadError不讀成沒有()
    {
        var f = F(Collect(certs: () => null), DataSecurityFactsService.EfsKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不是「沒有 EFS 憑證」", f.UnavailableReason);
    }

    // ── DS-004 Hello ────────────────────────────────────────────────────

    [Fact]
    public void Hello_只數註冊項_生物特徵永不讀()
    {
        var facts = Collect(hello: () => new List<string> { "db1", "db2", "db3" });
        var f = F(facts, DataSecurityFactsService.HelloKey);

        Assert.Equal(3, f.NumericValue);
        Assert.Contains("生物特徵永不讀", f.Value);
    }

    [Fact]
    public void Hello_讀不到_ReadError()
    {
        var f = F(Collect(hello: () => null), DataSecurityFactsService.HelloKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DS-006 自動註冊 ─────────────────────────────────────────────────

    [Fact]
    public void 憑證自動註冊_開啟與關閉分開陳述()
    {
        var on = F(Collect(flags: (p, v) => p.Contains("AutoEnrollment")), DataSecurityFactsService.AutoKey);
        Assert.Contains("已啟用", on.Value);
        Assert.Contains("不表示已成功註冊", on.Value);

        var off = F(Collect(flags: (p, v) => false), DataSecurityFactsService.AutoKey);
        Assert.Contains("沒有政策", off.Value);
    }

    [Fact]
    public void 憑證自動註冊_讀取失敗_ReadError()
    {
        var f = F(Collect(flags: (p, v) => throw new IOException("x")), DataSecurityFactsService.AutoKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DS-007 自簽憑證 ─────────────────────────────────────────────────

    [Fact]
    public void 自簽憑證_計數_且明說不是危險判決()
    {
        var facts = Collect(certs: () => new List<DataSecurityFactsService.CertInfo>
        {
            Cert("LM/Root", true), Cert("LM/CA", false), Cert("LM/CA", true),
        });
        var f = F(facts, DataSecurityFactsService.SelfSignedKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("不是危險判決", f.Value);
    }

    // ── DS-008 DRM ──────────────────────────────────────────────────────

    [Fact]
    public void PlayReady_初始化與否如實()
    {
        Assert.Contains("已在本機初始化", F(Collect(flags: (p, v) => p.Contains("PlayReady")), DataSecurityFactsService.DrmKey).Value);
        Assert.Contains("尚未初始化", F(Collect(flags: (p, v) => false), DataSecurityFactsService.DrmKey).Value);
    }

    // ── DS-009 TLS ──────────────────────────────────────────────────────

    [Fact]
    public void TLS_有明文停用_逐項列出()
    {
        var f = F(Collect(flags: (p, v) => p.Contains("TLS 1.0") || p.Contains("TLS 1.1") ? false : false),
            DataSecurityFactsService.TlsKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("SSL 2.0", f.Value);   // enabled=false → 全部列入停用
        Assert.Contains("不是安全背書", f.Value);
    }

    [Fact]
    public void TLS_沒有明文停用_標Unknown不假裝()
    {
        var f = F(Collect(flags: (p, v) => true), DataSecurityFactsService.TlsKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不假裝", f.UnavailableReason);
    }

    // ── DS-010 SAC ──────────────────────────────────────────────────────

    [Fact]
    public void SAC_三種狀態如實對照()
    {
        foreach (var (value, text) in new[] { (2, "已啟用"), (1, "評估模式"), (0, "已關閉") })
        {
            var f = F(Collect(fileExists: _ => true, intFlags: (p, v) => value), DataSecurityFactsService.SacKey);
            Assert.Contains(text, f.Value);
        }
    }

    [Fact]
    public void SAC_平台不支援_標NotSupported()
    {
        var f = F(Collect(fileExists: _ => false, intFlags: (p, v) => null), DataSecurityFactsService.SacKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    // ── DS-011 裝置安裝政策 ─────────────────────────────────────────────

    [Fact]
    public void 裝置安裝政策_有政策逐條列出_沒有則0是值()
    {
        var withPolicy = F(Collect(policies: () => new List<(string, string)> { ("DenyUnspecified", "1") }),
            DataSecurityFactsService.DevPolKey);
        Assert.Contains("DenyUnspecified：1", withPolicy.Value);

        var empty = F(Collect(policies: () => []), DataSecurityFactsService.DevPolKey);
        Assert.Contains("0 是值", empty.Value);
    }

    [Fact]
    public void 裝置安裝政策_列舉失敗_ReadError()
    {
        var f = F(Collect(policies: () => throw new IOException("x")), DataSecurityFactsService.DevPolKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DS-012 摘要（分母不含自己）──────────────────────────────────────

    [Fact]
    public void 資料安全摘要_分母不含自己且不是分數()
    {
        var facts = Collect(certs: () => new List<DataSecurityFactsService.CertInfo> { Cert("LM/Root", false) },
            hello: () => new List<string> { "db" }, flags: (p, v) => false, intFlags: (p, v) => 0,
            fileExists: _ => true, policies: () => []);
        var f = F(facts, DataSecurityFactsService.SummaryKey);

        Assert.NotNull(f.NumericValue);   // 可陳述項數＝Present 數（不含自己）
        Assert.Contains("不是安全分數", f.Value);
        Assert.DoesNotContain(DataSecurityFactsService.SummaryKey, f.Value);
    }

    [Fact]
    public void 整組_非Present都帶原因()
    {
        var facts = Collect(certs: () => null, hello: () => null, flags: (p, v) => throw new IOException("x"),
            intFlags: (p, v) => null, fileExists: _ => false, policies: () => throw new IOException("x"));
        foreach (var f in facts.Where(f => f.Availability != FactAvailability.Present))
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺三態原因");
    }
}
