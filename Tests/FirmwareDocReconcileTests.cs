using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 階段二矩陣與對帳事實的守門（ULTIMATE-1000 階段二 P2-105）。
///
/// <para>
/// <b>這一組守的是「申報的數字不是憑印象寫的」。</b>
/// <see cref="FirmwareDocReconcileFactsService"/> 把矩陣的三種狀態計數寫成
/// 編譯期常數——那是刻意的（發佈版身邊沒有 <c>docs/</c>，執行期讀不到時回 0
/// 會被讀成「全部完成」）。但常數會腐爛：<b>做完一項、改了矩陣，卻忘了改常數</b>，
/// 於是畫面繼續說「還有 25 把待辦」，而那 25 把裡有一把早就做完了。
/// 所以每一條常數都在這裡被文件重算一次。
/// </para>
///
/// <para>
/// <b>重算的方式與 <c>SpecImplementationReconciliationTests</c> 一致：</b>
/// 只認 <c>| ★?P2-nnn |</c> 開頭的資料列、欄位數必須是 9，
/// 並且只把「實作鍵」欄（第 8 格）以全形斜線 <c>／</c> 切開逐格計數。
/// 兩套工具各自解讀同一份文件是風險，但這裡<b>不能</b>共用那一套——
/// 那一套的職責是「矩陣每一格都要在目錄裡找得到」，而這一組的職責是「數字對得上」；
/// 共用會讓其中一邊的規則改動時，另一邊安靜地跟著變。
/// </para>
/// </summary>
public class FirmwareDocReconcileTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static readonly Regex MatrixRow = new(@"^\|\s*★?\s*(P2-\d{3})\s*\|", RegexOptions.Compiled);

    private static (int Items, int Ok, int Alias, int Todo) CountMatrix()
    {
        string path = Path.Combine(RepoRoot(), "docs", "ULTIMATE-1000", "STAGE-02-FIRMWARE.md");
        Assert.True(File.Exists(path), $"找不到階段二矩陣：{path}");

        int items = 0, ok = 0, alias = 0, todo = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            if (!MatrixRow.IsMatch(line)) continue;
            var cells = line.Split('|');
            // 欄位數不對的列不靜默跳過——那會讓「工具壞掉」變成「數字變小」。
            Assert.Equal(9, cells.Length);
            items++;

            foreach (string entry in cells[8].Split('／', StringSplitOptions.RemoveEmptyEntries))
            {
                string e = entry.Trim();
                if (e.StartsWith("ok:", StringComparison.Ordinal)) ok++;
                else if (e.StartsWith("alias:", StringComparison.Ordinal)) alias++;
                else if (e.StartsWith("todo:", StringComparison.Ordinal)) todo++;
                else Assert.Fail($"矩陣有一格不是 ok:／alias:／todo:：{e}");
            }
        }
        return (items, ok, alias, todo);
    }

    [Fact]
    public void 矩陣的三類鍵數要與申報常數一致()
    {
        var (_, ok, alias, todo) = CountMatrix();

        Assert.True(ok == FirmwareDocReconcileFactsService.ImplementedKeys,
            $"矩陣的 ok 鍵是 {ok} 把，申報常數寫 {FirmwareDocReconcileFactsService.ImplementedKeys} 把"
            + "——做完一項就要同時改矩陣與常數（還有這把鍵的文字）。");
        Assert.Equal(FirmwareDocReconcileFactsService.AliasKeys, alias);
        Assert.Equal(FirmwareDocReconcileFactsService.TodoKeys, todo);
    }

    /// <summary>
    /// 項數與鍵數是兩個不同的數字：一列可以宣告多把鍵（P2-014、P2-017、P2-029…）。
    /// 這條把兩者釘在一起，也阻止有人把「105」誤當成鍵數。
    /// </summary>
    [Fact]
    public void 項數與宣告鍵數的關係要說得出來()
    {
        var (items, ok, alias, todo) = CountMatrix();

        Assert.Equal(FirmwareDocReconcileFactsService.Items, items);
        Assert.Equal(FirmwareDocReconcileFactsService.DeclaredKeys, ok + alias + todo);
        Assert.True(FirmwareDocReconcileFactsService.DeclaredKeys > FirmwareDocReconcileFactsService.Items,
            "階段二有多列宣告兩把以上的鍵；鍵數不可能等於或小於項數");
    }

    [Fact]
    public void 事實的數值與文字要與常數一致()
    {
        var at = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var f = FirmwareDocReconcileFactsService.Fact(at);

        Assert.Equal("fw.doc.reconcile", f.Key);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal((double?)FirmwareDocReconcileFactsService.ImplementedKeys, f.NumericValue);
        Assert.Contains($"階段二規格 {FirmwareDocReconcileFactsService.Items} 項", f.Value);
        Assert.Contains($"{FirmwareDocReconcileFactsService.ImplementedKeys} 把已實作", f.Value);
        Assert.Contains($"{FirmwareDocReconcileFactsService.AliasKeys} 把以既有鍵別名收錄", f.Value);
        Assert.Contains($"{FirmwareDocReconcileFactsService.TodoKeys} 把待辦", f.Value);
    }

    /// <summary>
    /// <c>Present</c> 的事實沒有「為什麼讀不到」可言（<see cref="HardwareFact"/> 的第二條不變式）。
    /// 這一條在這裡特別重要：對帳結果是<b>答案</b>，不是讀取失敗——
    /// 把「還有 25 把待辦」寫進理由欄，會讓它在匯出快照時被當成缺值處理。
    /// </summary>
    [Fact]
    public void 對帳結果不得帶讀不到的理由()
    {
        var f = FirmwareDocReconcileFactsService.Fact(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 標題與事實用的是同一組數字()
    {
        var f = FirmwareDocReconcileFactsService.Fact(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        string headline = FirmwareDocReconcileFactsService.Headline;

        Assert.Contains($"{FirmwareDocReconcileFactsService.ImplementedKeys}／", headline);
        Assert.Contains($"{FirmwareDocReconcileFactsService.DeclaredKeys} 把", headline);
        Assert.Contains(FirmwareDocReconcileFactsService.AliasKeys.ToString(), headline);
        Assert.Contains(FirmwareDocReconcileFactsService.TodoKeys.ToString(), headline);
        Assert.Contains(FirmwareDocReconcileFactsService.TodoKeys.ToString(), f.Value);
    }
}
