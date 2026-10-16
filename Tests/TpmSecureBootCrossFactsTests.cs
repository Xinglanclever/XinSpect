using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TPM 與 Secure Boot 對照的守門（ULTIMATE-1000 階段二 P2-073）。
///
/// <para>
/// <b>核心是「可疑不是錯誤」</b>，以及兩個端點的讀不到不猜。
/// </para>
/// </summary>
public class TpmSecureBootCrossFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static TpmSecureBootCrossFactsService.CrossInput In(
        bool? tpm, bool? sb, string? endpoint = "uefi.secure_boot")
        => new(tpm, sb, endpoint);

    // ── 三態 ────────────────────────────────────────────────────────────


    /// <summary>
    /// 服務檔的原始碼，<b>去掉強調與參照標記</b>後回傳。
    ///
    /// <para>
    /// <b>為什麼：</b>「本服務不做判決」是<b>這一項的界線宣告</b>，
    /// 而 <c>HardwareFact</c> 的第二條不變式（<c>Present</c> ⇒ 說明併進
    /// <c>Value</c>）與 <c>HardwareSnapshotService</c> 的「可用的事實不得帶
    /// unavailableReason」合起來要求這種話不該放在事實的資料欄位裡。
    /// </para>
    ///
    /// <para>
    /// 而去標記是因為<b>兩邊的基準要一致</b>：服務檔的註解用 XML 的
    /// <c>&lt;b&gt;</c>，而斷言的字串多是 markdown 的 <c>**…**</c>。
    /// </para>
    /// </summary>
    private static string ServiceSource()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(
                   System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, "Services", "TpmSecureBootCrossFactsService.cs"));
        text = text.Replace("<b>", "").Replace("</b>", "").Replace("**", "");
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"</?c>|<see cref=""([^""]+)""\s*/>",
            m => m.Groups[1].Success ? m.Groups[1].Value : "");
    }

    [Fact]
    public void 沒有輸入時要標不支援並說出不自己讀()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, null).Single();

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("不自己去讀 TPM 或 Secure Boot", f.UnavailableReason);
    }

    [Fact]
    public void SecureBoot端點讀不到時要說無從對照()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(true, null, null)).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("兩個端點都讀不到，所以無從對照", f.UnavailableReason);
        Assert.Contains("「讀不到」與「關閉」是不同的斷言", f.UnavailableReason);
    }

    [Fact]
    public void TPM讀不到時要說無從對照並說出SB那邊的值()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(null, true)).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("TPM 那一邊讀不到，所以無從對照", f.UnavailableReason);
        Assert.Contains("uefi.secure_boot 得知為 開啟", f.UnavailableReason);
        Assert.Contains("「沒有 TPM」與「讀不到 TPM」是不同的斷言", f.UnavailableReason);
    }

    // ── 四個組合 ────────────────────────────────────────────────────────

    [Fact]
    public void 兩者都到位時要說那不代表量測開機在運作()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(true, true)).Single();

        Assert.StartsWith("兩者都到位", f.Value);
        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("不代表量測開機已經在運作", f.Value);
        Assert.Contains("見 bt.measured_boot", f.Value);
    }

    /// <summary>這一條守著「TPM 在而 SB 關」不是矛盾。</summary>
    [Fact]
    public void 有TPM而SB關閉時要說那不是矛盾()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(true, false)).Single();

        Assert.StartsWith("有 TPM 而 Secure Boot 關閉", f.Value);
        Assert.Equal(0.0, f.NumericValue);
        Assert.Contains("那不是矛盾", f.Value);
        Assert.Contains("TPM 與 Secure Boot 不是互相依賴的", f.Value);
    }

    /// <summary>
    /// <b>這一條守著本項最重要的一句。</b>
    /// 沒有 TPM 而 Secure Boot 開值得注意，但**不是錯誤**。
    /// </summary>
    [Fact]
    public void 沒有TPM而SB開啟時要說值得注意但不是錯誤()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(false, true)).Single();

        Assert.Contains("沒有 TPM 而 Secure Boot 開啟", f.Value);
        Assert.Contains("這個組合值得注意，但不是錯誤", f.Value);
        Assert.Contains("存在 UEFI 變數裡，那不需要 TPM", f.Value);
        Assert.Contains("可以正常開啟 Secure Boot", f.Value);
    }

    [Fact]
    public void 沒有TPM而SB開啟時要指出量測面的後果()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(false, true)).Single();

        Assert.Contains("沒有 TPM 就沒有 PCR", f.Value);
        Assert.Contains("不會被量測進信任鏈", f.Value);
    }

    [Fact]
    public void 兩者都沒有時要說那是CSM開機的正常樣貌()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(false, false)).Single();

        Assert.StartsWith("兩者都沒有", f.Value);
        Assert.Contains("CSM／Legacy 開機的機器上是正常的樣貌", f.Value);
    }

    // ── 界線 ────────────────────────────────────────────────────────────

    /// <summary>這一條守著「這不是判決」。</summary>
    [Fact]
    public void 要說出本服務不做判決()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(true, true)).Single();

        // 服務檔的原文是「只說『這個組合值得注意』，不說『設定有問題』」——
        // **而斷言原本寫的是「這個組合是什麼」**，那是我改文案時的另一種說法。
        // **斷言的字串要跟著服務檔的實際用詞**，否則它測的是一個不存在的句子。
        Assert.Contains("只說「這個組合值得注意」，不說「設定有問題」", ServiceSource());
        Assert.Contains("不宣告「這台機器的安全設定正確或不正確」", ServiceSource());
    }

    // ── FromFacts ───────────────────────────────────────────────────────

    private static HardwareFact F(string key, string value,
        FactAvailability availability = FactAvailability.Present)
        => new(key, "c", "n", value, "", "s", FactTrustLevel.Measured, false, At,
            null, availability);

    [Fact]
    public void 從事實取值時讀不到不得當成沒有()
    {
        var input = TpmSecureBootCrossFactsService.FromFacts(
        [
            F("tpm.present", "可用（Windows TBS 仲介，唯讀查詢）"),
            F("uefi.secure_boot", "是"),
        ]);

        Assert.Equal(true, input.TpmPresent);
        Assert.Equal(true, input.SecureBootEnabled);
        Assert.Equal("uefi.secure_boot", input.SecureBootEndpoint);
    }

    [Fact]
    public void 非Present的事實要回null而不是false()
    {
        var input = TpmSecureBootCrossFactsService.FromFacts(
        [
            F("tpm.present", "", FactAvailability.InsufficientPrivilege),
            F("uefi.secure_boot", "", FactAvailability.InsufficientPrivilege),
        ]);

        Assert.Null(input.TpmPresent);
        Assert.Null(input.SecureBootEnabled);
        Assert.Null(input.SecureBootEndpoint);
    }

    [Fact]
    public void UEFI端點優先於登錄檔端點()
    {
        var input = TpmSecureBootCrossFactsService.FromFacts(
        [
            F("tpm.present", "可用"),
            F("platform.secure_boot", "開啟"),
            F("uefi.secure_boot", "否"),
        ]);

        Assert.Equal(false, input.SecureBootEnabled);
        Assert.Equal("uefi.secure_boot", input.SecureBootEndpoint);
    }

    [Fact]
    public void UEFI端點讀不到時退回登錄檔端點並說出來源()
    {
        var input = TpmSecureBootCrossFactsService.FromFacts(
        [
            F("tpm.present", "可用"),
            F("platform.secure_boot", "開啟"),
            F("uefi.secure_boot", "", FactAvailability.InsufficientPrivilege),
        ]);

        Assert.Equal(true, input.SecureBootEnabled);
        Assert.Equal("platform.secure_boot", input.SecureBootEndpoint);
    }

    [Fact]
    public void 理由要說出SecureBoot的值來自哪個端點()
    {
        var f = TpmSecureBootCrossFactsService.Collect(At, In(true, true, "platform.secure_boot")).Single();

        Assert.Contains("Secure Boot 的值來自 platform.secure_boot", f.Value);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var f = TpmSecureBootCrossFactsService.Collect(At, In(true, true)).Single();

        Assert.True(catalog.Contains(f.Key),
            $"TPM／SB 對照產出了目錄裡沒有的鍵：{f.Key}");
    }
}
