using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 微碼版本雙來源對帳的守門（ULTIMATE-1000 階段二 P2-049）。
///
/// <para>
/// <b>核心是「兩者不同不必然是錯的」。</b>OS 更新微碼之後登錄值會更新，
/// 而 MSR 是現在生效的那一版——所以不同可能是正常的，也可能是異常的，
/// 而本服務看不到分辨所需的那部分（載入順序與平台政策）。
/// </para>
/// </summary>
public class MicrocodeVersionFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static MicrocodeVersionFactsService.Inputs In(uint? msr = null, uint? reg = null)
        => new(msr, reg);

    // ── MSR ─────────────────────────────────────────────────────────────

    [Fact]
    public void 讀不到MSR時要說那不代表沒有微碼()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(reg: 0x100))
            .Single(x => x.Key == MicrocodeVersionFactsService.MsrKey);

        Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
        Assert.Contains("這不代表沒有微碼", f.UnavailableReason);
        Assert.Contains("本專案不安裝驅動", f.UnavailableReason);
    }

    [Fact]
    public void MSR回報零要說零不是一個版本()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0))
            .Single(x => x.Key == MicrocodeVersionFactsService.MsrKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("0 不是一個微碼版本", f.UnavailableReason);
        Assert.Contains("還沒被寫入", f.UnavailableReason);
    }

    [Fact]
    public void MSR有值時要說那是CPU自己回的()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2A))
            .Single(x => x.Key == MicrocodeVersionFactsService.MsrKey);

        Assert.StartsWith("0x2A", f.Value);
        Assert.Contains("現在實際生效的", f.Value);
        Assert.Contains("不是任何軟體宣告的", f.Value);
    }

    // ── 登錄檔 ──────────────────────────────────────────────────────────

    [Fact]
    public void 沒有登錄值要與值為零分開()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2A))
            .Single(x => x.Key == MicrocodeVersionFactsService.RegistryKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("「沒有這個值」與「值是 0」是不同的斷言", f.UnavailableReason);
        Assert.Contains("OS 沒有記錄微碼載入", f.UnavailableReason);
    }

    [Fact]
    public void 登錄值要說那是OS的紀錄不是即時讀值()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(reg: 0x100))
            .Single(x => x.Key == MicrocodeVersionFactsService.RegistryKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("OS 載入時拿到的", f.Value);
        Assert.Contains("不是即時讀值", f.Value);
    }

    // ── 對帳 ────────────────────────────────────────────────────────────

    [Fact]
    public void 兩邊相同時要說那不代表微碼來自BIOS()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2A, reg: 0x2A))
            .Single(x => x.Key == MicrocodeVersionFactsService.CrossKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("兩邊一致：0x2A", f.Value);
        Assert.Contains("不代表微碼來自 BIOS", f.Value);
    }

    [Fact]
    public void 兩邊不同時要說那不必然是錯的()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2B, reg: 0x2A))
            .Single(x => x.Key == MicrocodeVersionFactsService.CrossKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不必然是錯的", f.UnavailableReason);
        Assert.Contains("只列出差異，不判斷誰對", f.UnavailableReason);
    }

    [Fact]
    public void MSR較新時要標出來()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2B, reg: 0x2A))
            .Single(x => x.Key == MicrocodeVersionFactsService.CrossKey);

        Assert.Contains("MSR 較新", f.Value);
    }

    [Fact]
    public void 登錄較新時要標出來()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2A, reg: 0x2B))
            .Single(x => x.Key == MicrocodeVersionFactsService.CrossKey);

        Assert.Contains("登錄檔較新", f.Value);
    }

    [Fact]
    public void 只有一邊有值時要說無從對照並說出是哪一邊()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2A))
            .Single(x => x.Key == MicrocodeVersionFactsService.CrossKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("只有 MSR 那一邊有值", f.Value);
        Assert.Contains("無從對照」而不是「不一致", f.UnavailableReason);
        Assert.Contains("製造假的落差", f.UnavailableReason);
    }

    [Fact]
    public void 兩邊都沒有時要標不支援()
    {
        var f = MicrocodeVersionFactsService.Collect(At, In())
            .Single(x => x.Key == MicrocodeVersionFactsService.CrossKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("無從對帳", f.UnavailableReason);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = MicrocodeVersionFactsService.Collect(At, In(msr: 1, reg: 1))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = MicrocodeVersionFactsService.Collect(At, In())
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = MicrocodeVersionFactsService.Collect(At, In(msr: 0x2A, reg: 0x2A));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "微碼版本事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
