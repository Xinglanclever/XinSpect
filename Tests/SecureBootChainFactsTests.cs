using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 安全開機四環（U10-SE-0001／SE-0002，第 314 輪）：這一支只做「併起來陳述」，
/// 所以測試的重點是<b>邊界</b>——「空著」與「讀不到」不能混成一句、Setup Mode 讀不到時不能
/// 假裝四環已鎖定、值裡必須留下「不評價金鑰」的界線。
/// </summary>
public class SecureBootChainFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string? reason = null)
        => new(key, "安全開機", key, value, "", "測試", FactTrustLevel.Reported, false, At,
            numeric, availability, reason);

    private static HardwareFact Kek(int n) => F("uefi.kek.count", $"{n} 條簽章（1 條清單）、100 bytes、NV", n);
    private static HardwareFact Db(int n) => F("uefi.db.count", $"{n} 條簽章（1 條清單）、100 bytes、NV", n);
    private static HardwareFact Dbx(int n) => F("uefi.dbx.count", $"{n} 條簽章（1 條清單）、100 bytes、NV", n);
    private static HardwareFact Pk() => F("uefi.pk", "存在（886 bytes）、NV", 886);
    private static HardwareFact SetupDeployed() => F("uefi.setup_mode", "金鑰已部署（Setup Mode 關閉）");
    private static HardwareFact SetupOpen() => F("uefi.setup_mode", "金鑰未部署（Setup Mode 開啟）");

    private static List<HardwareFact> AllPresent(int dbx = 3, bool pk = true)
    {
        var facts = new List<HardwareFact> { Kek(2), Db(4), Dbx(dbx), SetupDeployed() };
        if (pk) facts.Add(Pk());
        return facts;
    }

    private static HardwareFact Chain(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(SecureBootChainFactsService.Collect(facts, At),
            f => f.Key == SecureBootChainFactsService.ChainKey);

    [Fact]
    public void 四環都有內容時總覽要說四環齊備而且給出環數()
    {
        var chain = Chain(AllPresent());
        Assert.Equal(FactAvailability.Present, chain.Availability);
        Assert.Equal(4, chain.NumericValue);
        Assert.Contains("四環齊備", chain.Value);
    }

    [Fact]
    public void 某一環讀不到時不得說齊備且要指名是哪一環()
    {
        var facts = AllPresent();
        facts.RemoveAll(f => f.Key == "uefi.db.count");
        var chain = Chain(facts);

        Assert.Equal(FactAvailability.Unknown, chain.Availability);
        Assert.Contains("db（允許簽章資料庫）", chain.UnavailableReason);
        Assert.Contains("不能說四環齊備", chain.Value);
        Assert.DoesNotContain("四環齊備——", chain.Value);
    }

    [Fact]
    public void 讀取錯誤也算讀不到而不是空著()
    {
        var facts = AllPresent();
        facts.RemoveAll(f => f.Key == "uefi.kek.count");
        facts.Add(F("uefi.kek", "", null, FactAvailability.ReadError, "權限不足"));

        var chain = Chain(facts);
        Assert.Equal(FactAvailability.Unknown, chain.Availability);
        Assert.DoesNotContain(SecureBootChainFactsService.EmptyPrefix + "kek",
            SecureBootChainFactsService.Collect(facts, At).Select(f => f.Key));
    }

    [Fact]
    public void 空著的環要另外產生一筆事實而且值要說明白()
    {
        var facts = AllPresent(dbx: 0);
        var all = SecureBootChainFactsService.Collect(facts, At);

        var empty = Assert.Single(all, f => f.Key == SecureBootChainFactsService.EmptyPrefix + "dbx");
        Assert.Equal(FactAvailability.Present, empty.Availability);
        Assert.Null(empty.UnavailableReason);
        Assert.Contains("不是讀不到", empty.Value);
        Assert.Contains("dbx（撤銷簽章資料庫）", empty.Value);

        // 空著是已知狀態：總覽仍是 Present（不是 Unknown），環數少一。
        var chain = Assert.Single(all, f => f.Key == SecureBootChainFactsService.ChainKey);
        Assert.Equal(FactAvailability.Present, chain.Availability);
        Assert.Equal(3, chain.NumericValue);
        Assert.Contains("1 環空著", chain.Value);
    }

    [Fact]
    public void 多個空的環要各產生一筆且總數對得起來()
    {
        var facts = new List<HardwareFact> { Kek(0), Db(0), Dbx(0), Pk(), SetupDeployed() };
        var all = SecureBootChainFactsService.Collect(facts, At);

        Assert.Equal(3, all.Count(f => f.Key.StartsWith(SecureBootChainFactsService.EmptyPrefix, StringComparison.Ordinal)));
        Assert.Equal(1, all.Single(f => f.Key == SecureBootChainFactsService.ChainKey).NumericValue);
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void SetupMode開啟時要照抄來源值並說出信任根未立()
    {
        var chain = Chain(AllPresent().Where(f => f.Key != "uefi.setup_mode").Append(SetupOpen()).ToList());
        Assert.Contains("金鑰未部署", chain.Value);
        Assert.Contains("Setup Mode", chain.Value);
        // 四環內容都讀到了，所以總覽仍是 Present——「未鎖定」是內容而不是讀不到。
        Assert.Equal(FactAvailability.Present, chain.Availability);
    }

    [Fact]
    public void SetupMode讀不到時總覽要轉Unknown並說明少了什麼判斷()
    {
        var facts = AllPresent().Where(f => f.Key != "uefi.setup_mode").ToList();
        var chain = Chain(facts);

        Assert.Equal(FactAvailability.Unknown, chain.Availability);
        Assert.Contains("uefi.setup_mode", chain.UnavailableReason);
        Assert.Contains("Setup Mode 讀不到", chain.Value);
    }

    [Fact]
    public void 完全沒有輸入時要說讀不到而不是抛例外()
    {
        var facts = SecureBootChainFactsService.Collect([], At);
        var chain = Assert.Single(facts, f => f.Key == SecureBootChainFactsService.ChainKey);
        Assert.Equal(FactAvailability.Unknown, chain.Availability);
        Assert.False(string.IsNullOrWhiteSpace(chain.UnavailableReason));
        // 非 Present 的事實不帶數字（六態契約：非 Present 一律 null）
        Assert.Null(chain.NumericValue);
    }

    [Fact]
    public void 六態契約_Present不得帶理由_Unknown必須帶理由()
    {
        foreach (var facts in new[]
                 {
                     AllPresent(),
                     AllPresent(dbx: 0),
                     AllPresent().Where(f => f.Key != "uefi.pk").ToList(),
                     [],
                 })
        {
            foreach (var fact in SecureBootChainFactsService.Collect(facts, At))
            {
                if (fact.Availability == FactAvailability.Present) Assert.Null(fact.UnavailableReason);
                else Assert.False(string.IsNullOrWhiteSpace(fact.UnavailableReason));
            }
        }
    }

    [Fact]
    public void 空著家族的鍵要落在已登記的動態家族裡()
    {
        var all = SecureBootChainFactsService.Collect(AllPresent(dbx: 0), At);
        var key = all.Single(f => f.Key.StartsWith(SecureBootChainFactsService.EmptyPrefix, StringComparison.Ordinal)).Key;
        Assert.True(FactKeyDynamicCatalog.Matches(key), $"{key} 沒有被動態目錄收下");
        var id = key[SecureBootChainFactsService.EmptyPrefix.Length..];
        Assert.Matches("^[a-z0-9._-]+$", id);
    }

    [Fact]
    public void 值裡要留下不評價金鑰的界線()
    {
        var chain = Chain(AllPresent());
        Assert.Contains("不評價金鑰內容", chain.Value);
        Assert.Contains("不等於設定正確", chain.Value);
        Assert.Contains("沒有讀 VendorKeys", chain.Value);
    }

    [Fact]
    public void PK空著與PK讀不到都是讀不到_不會被當成零條()
    {
        // uefi.pk 只有 Present（>16 bytes）才算讀到；NotSupported（≤16 bytes 或讀失敗）一律算讀不到。
        var facts = AllPresent(pk: false);
        facts.Add(F("uefi.pk", "", null, FactAvailability.NotSupported, "PK 存在但長度 ≤16 bytes"));

        var chain = Chain(facts);
        Assert.Equal(FactAvailability.Unknown, chain.Availability);
        Assert.Contains("PK（平台金鑰）", chain.UnavailableReason);
        Assert.DoesNotContain(
            SecureBootChainFactsService.EmptyPrefix + "pk",
            SecureBootChainFactsService.Collect(facts, At).Select(f => f.Key));
    }
}
