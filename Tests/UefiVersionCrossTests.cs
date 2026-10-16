using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 映像與韌體版本一致性的守門（ULTIMATE-1000 階段二 P2-050）。
///
/// <para>
/// <b>核心是「無從對照」永遠不可以被算成「不一致」</b>，
/// 以及「不給一致率」那句話必須出現在每一個分支裡。
/// </para>
/// </summary>
public class UefiVersionCrossTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static UefiVersionCrossFactsService.EmbeddedVersion V(string text, ushort build = 1,
        string? ui = null, string guid = "AAAA0000-0000-0000-0000-000000000000")
        => new(0, guid, ui, build, text);

    // ── 版本區段解碼 ────────────────────────────────────────────────────

    /// <summary>
    /// 造一個 VERSION 區段的內容。
    ///
    /// <para>
    /// <b>大小由資料決定，不由我算。</b>第一版我寫的是
    /// <c>new byte[2 + 8]</c> 然後放 <c>"1.2.3\0"</c>——
    /// 那是 12 個 bytes，而緩衝區只有 10 個。
    /// <b>同一個「起點 vs 起點＋寬度」的錯誤，這是第五次</b>
    /// （Type0、Boot、Type19、Type4 各一次）。
    /// </para>
    ///
    /// <para>
    /// 前四次我都直接改數字，而這次改成讓大小跟著資料走——
    /// <b>沒有算，就沒有算錯的可能。</b>
    /// </para>
    /// </summary>
    private static byte[] VersionPayload(string text, ushort build = 1)
    {
        var str = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        var payload = new byte[2 + str.Length];
        BitConverter.GetBytes(build).CopyTo(payload, 0);
        str.CopyTo(payload, 2);
        return payload;
    }

    [Fact]
    public void 版本區段要解出BuildNumber與字串()
    {
        var d = UefiVersionCrossFactsService.DecodeVersionSection(VersionPayload("1.2.3", 42));

        Assert.NotNull(d);
        Assert.Equal(42, d!.Value.BuildNumber);
        Assert.Equal("1.2.3", d.Value.Text);
    }

    [Fact]
    public void 沒有補零終止時要讀到緩衝區尾端為止()
    {
        // 刻意不放補零：大小就是字串本身的長度。
        var str = System.Text.Encoding.Unicode.GetBytes("ABC");
        var payload = new byte[2 + str.Length];
        BitConverter.GetBytes((ushort)1).CopyTo(payload, 0);
        str.CopyTo(payload, 2);

        var d = UefiVersionCrossFactsService.DecodeVersionSection(payload);

        Assert.Equal("ABC", d!.Value.Text);
    }

    [Fact]
    public void 太短的區段要如實回null()
    {
        Assert.Null(UefiVersionCrossFactsService.DecodeVersionSection([]));
        Assert.Null(UefiVersionCrossFactsService.DecodeVersionSection([0x01, 0x00]));
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有掃描結果時要標不支援並說出不自己讀BIOS區()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.0", null);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Equal(FactAvailability.NotSupported, c.Availability);
        Assert.Contains("不自己去讀 BIOS 區", c.UnavailableReason);
    }

    [Fact]
    public void 映像沒有版本區段時要標不適用並說出無從對照()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.0", []);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Null(c.NumericValue);
        Assert.Contains("「無從對照」不是「不一致」", c.UnavailableReason);
    }

    [Fact]
    public void 韌體版本拿不到時要標不適用並說缺一邊就沒有對照()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, null, [V("1.0")]);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Null(c.NumericValue);
        Assert.Contains("「無從對照」不是「不一致」", c.UnavailableReason);
        Assert.Contains("缺一邊就沒有對照可言", c.UnavailableReason);
        Assert.Contains("會讓一台正常的機器看起來像版本不符", c.UnavailableReason);
    }

    // ── 一致 ────────────────────────────────────────────────────────────

    [Fact]
    public void 版本相同時要標一致()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.2.3", [V("1.2.3")]);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Equal(FactAvailability.Present, c.Availability);
        Assert.Equal(1.0, c.NumericValue);
        Assert.Contains("一致", c.Value);
    }

    [Fact]
    public void 一致時要說出那不代表正確版本()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.2.3", [V("1.2.3")]);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Contains("不代表它是「正確版本」", c.UnavailableReason);
        Assert.Contains("只代表它們沒有互相矛盾", c.UnavailableReason);
        Assert.Contains("要判斷正確性需要原廠映像", c.UnavailableReason);
    }

    [Fact]
    public void 比對要忽略大小寫與前後空白()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, " 1.2.3 ", [V("1.2.3")]);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Equal(FactAvailability.Present, c.Availability);
    }

    // ── 不一致 ──────────────────────────────────────────────────────────

    [Fact]
    public void 版本不同時要標不一致並列出兩邊()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.2.3", [V("2.0.0")]);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Equal(0.0, c.NumericValue);
        Assert.Contains("不一致", c.Value);
        Assert.Contains("2.0.0", c.Value);
        Assert.Contains("1.2.3", c.Value);
    }

    [Fact]
    public void 不一致時要說出那不等於有問題且本服務不判斷誰對()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.2.3", [V("2.0.0")]);
        var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);

        Assert.Contains("不一致不等於有問題", c.UnavailableReason);
        Assert.Contains("本來就描述不同的東西", c.UnavailableReason);
        Assert.Contains("本服務指出差異，不判斷誰對", c.UnavailableReason);
    }

    // ── 政策語句 ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>「不給一致率」必須出現在每一個分支。</b>
    /// 一個只在「有對照」時才出現的聲明，會在沒有對照時被誤讀成默許。
    /// （這是 P2-033 修過一次的地方。）
    /// </summary>
    [Fact]
    public void 不給一致率那句話要在每一個分支裡()
    {
        var cases = new[]
        {
            UefiVersionCrossFactsService.Collect(At, "1.0", [V("1.0")]),   // 一致
            UefiVersionCrossFactsService.Collect(At, "1.0", [V("2.0")]),   // 不一致
            UefiVersionCrossFactsService.Collect(At, null, [V("1.0")]),    // 無從對照
        };

        foreach (var facts in cases)
        {
            var c = facts.Single(f => f.Key == UefiVersionCrossFactsService.CrossKey);
            Assert.Contains("本服務不給一致率", c.UnavailableReason);
            Assert.Contains("把「無從對照」偷渡成「不一致」", c.UnavailableReason);
        }
    }

    // ── 映像內嵌鍵 ──────────────────────────────────────────────────────

    [Fact]
    public void 映像內嵌鍵要列出每一個條目與Build編號()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.0",
            [V("1.0", build: 7, ui: "BiosDriver")]);
        var e = facts.Single(f => f.Key == UefiVersionCrossFactsService.EmbeddedKey);

        Assert.Contains("1.0（Build 7，FV#0／BiosDriver）", e.Value);
        Assert.Equal(1.0, e.NumericValue);
    }

    [Fact]
    public void 映像內嵌鍵要說出兩個來源獨立且相同不等於正確()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.0", [V("1.0")]);
        var e = facts.Single(f => f.Key == UefiVersionCrossFactsService.EmbeddedKey);

        Assert.Contains("與韌體在 SMBIOS 裡宣告的是兩個獨立來源", e.UnavailableReason);
        Assert.Contains("只代表這兩個來源沒有互相矛盾", e.UnavailableReason);
    }

    [Fact]
    public void 沒有版本區段時要說出可能藏在壓縮區段裡()
    {
        var facts = UefiVersionCrossFactsService.Collect(At, "1.0", []);
        var e = facts.Single(f => f.Key == UefiVersionCrossFactsService.EmbeddedKey);

        Assert.Equal(FactAvailability.NotApplicable, e.Availability);
        Assert.Null(e.NumericValue);
        Assert.Contains("可能它藏在壓縮區段裡", e.UnavailableReason);
        Assert.Contains("本版不遞迴進入", e.UnavailableReason);
    }

    // ── 從既有的解碼結果讀 ──────────────────────────────────────────────

    [Fact]
    public void 要能從既有的FV解碼結果讀出版本區段()
    {
        var payload = VersionPayload("X1.0", 9);

        var fv = UefiFvTestData.BuildFv(
        [
            UefiFvTestData.FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x07,
                [UefiFvTestData.UiSection("BiosDriver"),
                 UefiFvTestData.Section(UefiVersionCrossFactsService.SectTypeVersion, payload)]),
        ]);
        var fvs = UefiFv.DecodeFvs(fv);

        var embedded = UefiVersionCrossFactsService.ReadEmbedded(fvs, fv);

        var one = Assert.Single(embedded);
        Assert.Equal("X1.0", one.VersionString);
        Assert.Equal(9, one.BuildNumber);
        Assert.Equal("BiosDriver", one.UiName);
    }

    [Fact]
    public void 截斷的版本區段要被略過而不是丟例外()
    {
        var fv = UefiFvTestData.BuildFv(
        [
            UefiFvTestData.FfsFile("9B680FCE-AD6B-4F3A-B60B-F59899003443", 0x07,
                [UefiFvTestData.Section(UefiVersionCrossFactsService.SectTypeVersion, [0x01])]),
        ]);
        var fvs = UefiFv.DecodeFvs(fv);

        var embedded = UefiVersionCrossFactsService.ReadEmbedded(fvs, fv[..(fv.Length - 20)]);

        Assert.Empty(embedded);   // 越界的內容被略過，不是猜
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = UefiVersionCrossFactsService.Collect(At, "1.0", [V("1.0")])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = UefiVersionCrossFactsService.Collect(At, "1.0", null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = UefiVersionCrossFactsService.Collect(At, "1.0", [V("1.0")]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "版本一致性事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
