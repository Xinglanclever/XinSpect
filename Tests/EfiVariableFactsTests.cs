using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// EFI 變數簽章清單與屬性的守門（ULTIMATE-1000 階段二 P2-079／P2-080／P2-083）。
///
/// <para>
/// <b>核心有兩件事：</b>「沒權限讀」與「變數不存在」必須分開；
/// 而屬性的位元<b>不命名</b>。
/// </para>
/// </summary>
public class EfiVariableFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static EfiVariableFactsService.VariableRead V(string name, byte[]? bytes,
        uint attrs = 0x7, bool privileged = true)
        => new(name, bytes, attrs, privileged);

    /// <summary>造一條 EFI_SIGNATURE_LIST：型別 GUID ＋ 大小 ＋ header ＋ N 筆簽章。</summary>
    private static byte[] SigList(string typeGuid, int signatureCount, int sigSize = 48)
    {
        var guidBytes = Guid.Parse(typeGuid).ToByteArray();
        int total = 28 + signatureCount * sigSize;
        var buf = new byte[total];
        guidBytes.CopyTo(buf, 0);
        BitConverter.GetBytes((uint)total).CopyTo(buf, 16);
        BitConverter.GetBytes(0u).CopyTo(buf, 20);
        BitConverter.GetBytes((uint)sigSize).CopyTo(buf, 24);
        return buf;
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有讀取結果時要標不支援並說出不自己問韌體()
    {
        var facts = EfiVariableFactsService.Collect(At, null);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Equal(FactAvailability.NotSupported, k.Availability);
        Assert.Contains("不自己去問韌體", k.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著最重要的一句。</b>
    /// 沒權限讀與變數不存在是不同的斷言。
    /// </summary>
    [Fact]
    public void 沒權限讀要與變數不存在分開()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", null, privileged: false),
            V("KEK", null, privileged: false),
        ]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Equal(FactAvailability.InsufficientPrivilege, k.Availability);
        Assert.Null(k.NumericValue);
        Assert.Contains("「沒權限讀」與「變數不存在」是不同的斷言", k.UnavailableReason);
        Assert.Contains("AdjustTokenPrivileges 未持有權限時仍回 true", k.UnavailableReason);
        Assert.Contains("沒權限讀」被誤報成「變數不存在＝可能 Legacy 開機", k.UnavailableReason);
    }

    [Fact]
    public void 有權限但變數不存在要說那是CSM開機的正常樣貌()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", null), V("KEK", null), V("db", null), V("dbx", null),
        ]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Equal(FactAvailability.NotApplicable, k.Availability);
        Assert.Null(k.NumericValue);
        Assert.Contains("權限是通的", k.UnavailableReason);
        Assert.Contains("CSM／Legacy 開機的正常樣貌", k.UnavailableReason);
    }

    // ── 計數 ────────────────────────────────────────────────────────────

    [Fact]
    public void 要逐個變數報出bytes條數與簽章數()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1)),
            V("KEK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 3)),
            V("db", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 5)),
            V("dbx", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 200)),
        ]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Contains("PK 76 bytes／1 條清單／1 筆簽章", k.Value);
        Assert.Contains("KEK 172 bytes／1 條清單／3 筆簽章", k.Value);
        Assert.Contains("dbx 9628 bytes／1 條清單／200 筆簽章", k.Value);
        Assert.Equal(209.0, k.NumericValue);
    }

    [Fact]
    public void 部分變數不存在時要逐個說出()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1)),
            V("KEK", null), V("db", null), V("dbx", null),
        ]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Contains("KEK 不存在", k.Value);
        Assert.Contains("db 不存在", k.Value);
        Assert.Equal(1.0, k.NumericValue);
    }

    [Fact]
    public void 四個變數是同一種結構的四種用途()
    {
        var facts = EfiVariableFactsService.Collect(At,
            [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1))]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Contains("同一種結構的四種用途", k.UnavailableReason);
        Assert.Contains("拆成四把鍵只會讓「它們是同一種東西」這件事消失", k.UnavailableReason);
    }

    [Fact]
    public void 要說出這裡只報筆數不驗簽章()
    {
        var facts = EfiVariableFactsService.Collect(At,
            [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1))]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Contains("不是「那些簽章有效」", k.UnavailableReason);
        Assert.Contains("不驗簽章", k.UnavailableReason);
        Assert.Contains("那是 secure.dbx 的領域", k.UnavailableReason);
    }

    // ── 屬性 ────────────────────────────────────────────────────────────

    [Fact]
    public void 屬性要列出原始值與長度()
    {
        var facts = EfiVariableFactsService.Collect(At,
        [
            V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1), attrs: 0x00000027),
            V("KEK", null),
        ]);
        var a = facts.Single(f => f.Key == EfiVariableFactsService.AttrsKey);

        Assert.Contains("PK 屬性 0x00000027／76 bytes", a.Value);
        Assert.Contains("KEK 不存在", a.Value);
    }

    /// <summary>
    /// <b>這一條守著第三個「不命名」的判斷。</b>
    /// 屬性的位元不得被命名——即使是最有名的那些。
    /// </summary>
    [Fact]
    public void 屬性不得替任何位元命名()
    {
        var facts = EfiVariableFactsService.Collect(At,
            [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1), attrs: 0x27)]);
        var a = facts.Single(f => f.Key == EfiVariableFactsService.AttrsKey);

        Assert.DoesNotContain("NON_VOLATILE", a.Value);
        Assert.DoesNotContain("RUNTIME_ACCESS", a.Value);
        Assert.DoesNotContain("BOOTSERVICE", a.Value);
        Assert.DoesNotContain("EFI_VARIABLE", a.Value);
        Assert.DoesNotContain("唯讀", a.Value);
        Assert.DoesNotContain("執行期可寫", a.Value);
    }

    [Fact]
    public void 屬性的理由要說出我記得不是出處()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1))]);
        var a = facts.Single(f => f.Key == EfiVariableFactsService.AttrsKey);

        Assert.Contains("只列原始值", a.UnavailableReason);
        Assert.Contains("本專案沒有任何地方收錄那張表", a.UnavailableReason);
        Assert.Contains("「我記得」不是出處", a.UnavailableReason);
        Assert.Contains("填錯的位元會讓一個不存在的屬性看起來存在", a.UnavailableReason);
    }

    [Fact]
    public void 屬性的理由要說出前半做完了後半還沒()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1))]);
        var a = facts.Single(f => f.Key == EfiVariableFactsService.AttrsKey);

        Assert.Contains("UEFI Spec §8.2", a.UnavailableReason);
        Assert.Contains("「唯讀／執行期可寫」這個判斷需要位元名才做得出來", a.UnavailableReason);
        Assert.Contains("規格 P2-083 的後半在對準出處之前不算完成", a.UnavailableReason);
    }

    // ── 損毀與鍵集合 ────────────────────────────────────────────────────

    [Fact]
    public void 格式不符的變數內容要回零筆而不是丟例外()
    {
        var facts = EfiVariableFactsService.Collect(At, [V("PK", new byte[4])]);
        var k = facts.Single(f => f.Key == EfiVariableFactsService.KeysKey);

        Assert.Contains("PK 4 bytes／0 條清單／0 筆簽章", k.Value);
        Assert.Equal(0.0, k.NumericValue);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = EfiVariableFactsService.Collect(At, [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1))])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = EfiVariableFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = EfiVariableFactsService.Collect(At,
            [V("PK", SigList("C1C4F266-A914-414F-90EB-2B3A3D903F0D", 1))]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "EFI 變數事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
