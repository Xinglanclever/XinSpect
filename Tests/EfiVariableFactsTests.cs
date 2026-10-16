using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// EFI 變數屬性的守門（ULTIMATE-1000 階段二 P2-083）。
///
/// <para>
/// <b>範圍在撤回簽章清單那一半之後變得明確。</b>
/// 原本這裡也測 ev.pk.count（PK／KEK／db／dbx 的簽章計數），
/// 而那個已經有實作（UefiSignatureFactsService 的 uefi.pk／uefi.kek／
/// uefi.db／uefi.dbx），所以那一半的測試一併撤回了。
/// </para>
///
/// <para>
/// 留下來測兩件事：<b>「沒權限讀」與「變數不存在」必須分開</b>；
/// 而<b>屬性的位元不命名</b>。
/// </para>
/// </summary>
public class EfiVariableFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static EfiVariableFactsService.VariableRead V(string name, byte[]? bytes,
        uint attrs = 0x7, bool privileged = true)
        => new(name, bytes, attrs, privileged);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有讀取結果時要標不支援並說出不自己問韌體()
    {
        var facts = EfiVariableFactsService.Collect(At, null);
        var a = facts.Single();

        Assert.Equal(FactAvailability.NotSupported, a.Availability);
        Assert.Contains("不自己去問韌體", a.UnavailableReason);
    }

    /// <summary>這一條守著「兩個不同的沒有」。 </summary>
    [Fact]
    public void 沒權限讀要與變數不存在分開()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", null, privileged: false),
            V("KEK", null, privileged: false),
        ]);
        var a = facts.Single();

        Assert.Equal(FactAvailability.InsufficientPrivilege, a.Availability);
        Assert.Null(a.NumericValue);
        Assert.Contains("「沒權限讀」與「變數不存在」是不同的斷言", a.UnavailableReason);
        Assert.Contains("AdjustTokenPrivileges 未持有權限時仍回 true", a.UnavailableReason);
    }

    [Fact]
    public void 空的讀取清單要標不適用()
    {
        var facts = EfiVariableFactsService.Collect(At, []);
        var a = facts.Single();

        Assert.Equal(FactAvailability.NotApplicable, a.Availability);
        Assert.Null(a.NumericValue);
    }

    // ── 屬性 ────────────────────────────────────────────────────────────

    [Fact]
    public void 屬性要列出原始值與長度()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", new byte[76], attrs: 0x00000027),
            V("KEK", null),
        ]);
        var a = facts.Single();

        Assert.Contains("PK 屬性 0x00000027／76 bytes", a.Value);
        Assert.Contains("KEK 不存在", a.Value);
        Assert.Equal(1.0, a.NumericValue);
    }

    /// <summary>
    /// <b>這一條守著第三個「不命名」的判斷。</b>
    /// 屬性的位元不得被命名——即使是最有名的那些。
    /// </summary>
    [Fact]
    public void 屬性不得替任何位元命名()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[76], attrs: 0x27)]);
        var a = facts.Single();

        // **說明被不變式併進了 Value，而那段說明本來就會提到那幾個常數名**
        // （它講的正是「不命名」）——所以要驗的是值本身：第一個「。」之前的那一段。
        string raw = a.Value.Split('。')[0];
        Assert.DoesNotContain("NON_VOLATILE", raw);
        Assert.DoesNotContain("RUNTIME_ACCESS", raw);
        Assert.DoesNotContain("BOOTSERVICE", raw);
        Assert.DoesNotContain("EFI_VARIABLE", raw);
        Assert.DoesNotContain("唯讀", raw);
        Assert.DoesNotContain("執行期可寫", raw);
    }

    [Fact]
    public void 屬性的理由要說出我記得不是出處()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[76])]);
        var a = facts.Single();

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("只列原始值", a.Value);
        Assert.Contains("本專案沒有任何地方收錄那張表", a.Value);
        Assert.Contains("「我記得」不是出處", a.Value);
        Assert.Contains("填錯的位元會讓一個不存在的屬性看起來存在", a.Value);
    }

    [Fact]
    public void 屬性的理由要說出前半做完了後半還沒()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[76])]);
        var a = facts.Single();

        Assert.Contains("UEFI Spec §8.2", a.Value);
        Assert.Contains("「唯讀／執行期可寫」這個判斷需要位元名才做得出來", a.Value);
        Assert.Contains("規格 P2-083 的後半在對準出處之前不算完成", a.Value);
    }

    /// <summary>
    /// <b>這一條守著撤回本身。</b>
    /// 屬性的理由裡要說出簽章清單那一半為什麼不見了，
    /// 以及它現在在哪裡——否則下一個人會以為它還沒做。
    /// </summary>
    [Fact]
    public void 理由要說出簽章清單那一半已經撤回並指出它在哪()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[76])]);
        var a = facts.Single();

        Assert.Contains("簽章清單那一半已經撤回了", a.Value);
        Assert.Contains("UefiSignatureFactsService", a.Value);
        Assert.Contains("uefi.pk", a.Value);
        Assert.Contains("做得更完整", a.Value);
    }

    /// <summary>
    /// <b>這一條守著本服務不再產出簽章計數。</b>
    /// ev.pk.count 已經撤回——那個鍵不該再出現在輸出裡。
    /// </summary>
    [Fact]
    public void 不得再產出已撤回的簽章計數鍵()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[76])]);

        Assert.DoesNotContain("ev.pk.count", facts.Select(f => f.Key));
        Assert.Equal("ev.var.attrs", facts.Single().Key);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[76])]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "EFI 變數屬性事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
