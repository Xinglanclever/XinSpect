using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 信任根與 VBS 對照的守門（ULTIMATE-1000 階段二 P2-062）。
///
/// <para>
/// <b>核心是「三種信任根與 VBS 的關係不同，不能一句話帶過」</b>，
/// 以及「沒有直接關係」也要說出來。
/// </para>
/// </summary>
public class TrustRootVbsCrossFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static TrustRootVbsCrossFactsService.CrossInput In(
        TrustRootVbsCrossFactsService.VbsState vbs, bool? sb = null,
        bool? mb = null, bool? bg = null)
        => new(vbs, sb, mb, bg);

    [Fact]
    public void 沒有輸入時要標不支援並說出不自己讀()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At, null).Single();

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不自己去讀信任根或 VBS", f.UnavailableReason);
    }

    [Fact]
    public void VBS讀不到時要說無從對照()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Unknown, sb: true)).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("VBS 那一邊讀不到，所以無從對照", f.UnavailableReason);
        Assert.Contains("「讀不到」與「關閉」是不同的斷言", f.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著本項唯一有實質依賴的那一條。</b>
    /// </summary>
    [Fact]
    public void VBS開著而SecureBoot關著要指出那是唯一的實質依賴()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Running, sb: false)).Single();

        // 這三條都是 Present 事實的說明——而不變式把說明併進了 Value。
        Assert.Contains("本服務唯一有實質依賴的那一條", f.Value);
        Assert.Contains("VBS 的「安全啟動」保護在 Secure Boot 關閉時不能生效", f.Value);
    }

    [Fact]
    public void VBS開著而SecureBoot關著要說出那不是設定錯誤()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Running, sb: false)).Single();

        Assert.Contains("不代表設定錯誤", f.Value);
        Assert.Contains("VBS 可以在 Secure Boot 關閉時執行", f.Value);
        Assert.Contains("只是少了一層保護", f.Value);
        // 「不做判決」是**這一項的界線宣告**，不是這把鍵的值——
        // 而不變式要求 Present 的事實不得帶理由欄，所以它住在類別層的 XML 文件註解。
        // 斷言的字串要跟著服務檔的實際用詞：那裡寫的是「界線：這不是判決」。
        Assert.Contains("界線：這不是判決", ServiceSource());
    }

    [Fact]
    public void VBS開著而SecureBoot讀不到時要說無從判斷()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Running, sb: null)).Single();

        Assert.Contains("Secure Boot 那一邊讀不到", f.Value);
        Assert.Contains("無從判斷 VBS 的安全啟動保護有沒有依據", f.Value);
    }

    [Fact]
    public void VBS開著而SecureBoot開著要說安全啟動保護有依據()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Running, sb: true)).Single();

        Assert.Contains("Secure Boot 開著", f.Value);
        Assert.Contains("VBS 的安全啟動保護有依據", f.Value);
    }

    /// <summary>
    /// <b>這一條守著「沒有直接關係也要說出來」。</b>
    /// 否則讀者會以為沒說的那兩條有問題。
    /// </summary>
    [Fact]
    public void 要說出另外兩種信任根與VBS沒有直接關係()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Running, sb: true)).Single();

        Assert.Contains("其餘兩種信任根與 VBS 沒有直接關係，而那也要說出來", f.Value);
        Assert.Contains("否則讀者會以為沒說的那兩條有問題", f.Value);
        Assert.Contains("Boot Guard", f.Value);
        Assert.Contains("時間軸上不重疊", f.Value);
        Assert.Contains("TPM 量測開機", f.Value);
        Assert.Contains("是共同的**前提**，不是依賴關係", f.Value);
    }

    [Fact]
    public void VBS關閉時也要給出完整的理由()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Off, sb: true)).Single();

        // **值一定是「<狀態>。<說明>」**——說明是這一項的實質內容，不是附加物，
        // 所以比對前綴而不是全等。
        Assert.StartsWith("VBS 關閉", f.Value);
        Assert.Equal(0.0, f.NumericValue);
        Assert.Contains("沒有直接關係", f.Value);
    }

    [Fact]
    public void 已啟用但未執行也視為VBS開著()
    {
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.EnabledNotRunning, sb: false)).Single();

        Assert.StartsWith("VBS 已啟用但未執行", f.Value);
        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("唯一有實質依賴的那一條", f.Value);
    }

    /// <summary>
    /// 服務檔的原始碼，<b>去掉強調與參照標記</b>後回傳。
    ///
    /// <para>
    /// <b>為什麼要讀原始碼：</b>「這不是判決」是<b>這一項的界線宣告</b>——
    /// 而 <c>HardwareFact</c> 的第二條不變式（<c>Present</c> ⇒ 說明併進
    /// <c>Value</c>）與 <c>HardwareSnapshotService</c> 的「可用的事實不得帶
    /// unavailableReason」合起來要求這種話不該放在事實的資料欄位裡。
    /// <b>所以它的位置在類別層的 XML 文件註解</b>，而斷言要測的是
    /// 「那句話有沒有被寫下來」，不是「它放在哪一個欄位」。
    /// </para>
    ///
    /// <para>
    /// <b>而去標記是因為兩邊的基準要一致</b>：服務檔的註解用 XML 的
    /// <c>&lt;b&gt;</c>，而斷言的字串寫的是純文字。
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
            dir!.FullName, "Services", "TrustRootVbsCrossFactsService.cs"));
        text = text.Replace("<b>", "").Replace("</b>", "").Replace("**", "");
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"</?c>|<see cref=""([^""]+)""\s*/>",
            m => m.Groups[1].Success ? m.Groups[1].Value : "");
    }

    // ── FromFacts ───────────────────────────────────────────────────────

    private static HardwareFact F(string key, string value,
        FactAvailability availability = FactAvailability.Present)
        => new(key, "c", "n", value, "", "s", FactTrustLevel.Measured, false, At,
            null, availability);

    [Fact]
    public void 從事實取VBS四態()
    {
        foreach (var (text, expected) in new[]
        {
            ("執行中", TrustRootVbsCrossFactsService.VbsState.Running),
            ("已啟用但未執行", TrustRootVbsCrossFactsService.VbsState.EnabledNotRunning),
            ("關閉", TrustRootVbsCrossFactsService.VbsState.Off),
        })
        {
            var input = TrustRootVbsCrossFactsService.FromFacts([F("virt.vbs", text)]);
            Assert.Equal(expected, input.Vbs);
        }
    }

    [Fact]
    public void 非Present的VBS要回Unknown而不是Off()
    {
        var input = TrustRootVbsCrossFactsService.FromFacts(
            [F("virt.vbs", "—（讀不到）", FactAvailability.ReadError)]);

        Assert.Equal(TrustRootVbsCrossFactsService.VbsState.Unknown, input.Vbs);
    }

    [Fact]
    public void 從信任根型別取SecureBoot()
    {
        var input = TrustRootVbsCrossFactsService.FromFacts(
            [F("virt.vbs", "執行中"), F("bt.root.types", "Secure Boot、TPM 度量開機")]);

        Assert.Equal(true, input.SecureBoot);
        Assert.Equal(true, input.MeasuredBoot);
        Assert.Null(input.BootGuardAcm);
    }

    [Fact]
    public void 信任根讀不到時三種都回null()
    {
        var input = TrustRootVbsCrossFactsService.FromFacts(
            [F("virt.vbs", "執行中"), F("bt.root.types", "", FactAvailability.NotSupported)]);

        Assert.Null(input.SecureBoot);
        Assert.Null(input.MeasuredBoot);
        Assert.Null(input.BootGuardAcm);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var f = TrustRootVbsCrossFactsService.Collect(At,
            In(TrustRootVbsCrossFactsService.VbsState.Running, sb: true)).Single();

        Assert.True(catalog.Contains(f.Key),
            $"信任根／VBS 對照產出了目錄裡沒有的鍵：{f.Key}");
    }
}
