using System;
using System.IO;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 報告 diff（U10-PF-0002／<c>rep.diff.mode</c>）的守門：兩份合成報告逐鍵對照，
/// 新增／消失／值變化如實成列；<b>輸出只含鍵名與計數，不含任何值</b>——
/// 敏感值（序號、MAC、主機名）不會經由 diff 洩漏。三種匯出格式（HTML／MD／TXT）
/// 共用同一份中介表示，解析回來的鍵表也必須一致，跨格式互比才成立。
/// </summary>
public class ReportDiffServiceTests
{
    // ── 合成報告：與 ReportService 三個渲染器的形狀同構 ──────────────────────

    private const string MdBefore = """
        # 曦覽 XinSpect 硬體報告

        產生時間 2026-10-25 08:00:00 ・ 主機 OLD-HOST ・ Fake CPU

        ## 系統

        | 項目 | 內容 |
        | --- | --- |
        | 作業系統 | Windows 11 |
        | 主機名稱 | OLD-HOST |
        | 磁碟序號 | SERIAL-AAA |

        **已安裝模組**

        | 插槽 | 製造商 |
        | --- | --- |
        | DIMM_A1 | ACME |
        """;

    private const string MdAfter = """
        # 曦覽 XinSpect 硬體報告

        產生時間 2026-10-25 09:00:00 ・ 主機 NEW-HOST ・ Fake CPU

        ## 系統

        | 項目 | 內容 |
        | --- | --- |
        | 作業系統 | Windows 12 |
        | 主機名稱 | NEW-HOST |
        | 新韌體 | UEFI 2.0 |
        | 磁碟序號 | SERIAL-AAA |

        **已安裝模組**

        | 插槽 | 製造商 |
        | --- | --- |
        | DIMM_A1 | ACME |

        ## 螢幕與色域

        | 螢幕 | 製造商 |
        | --- | --- |
        | PANEL-1 | ViewSonic |
        """;

    [Fact]
    public void 兩份Markdown報告_新增消失變化逐鍵成列()
    {
        var d = ReportDiffService.Compare(MdBefore, MdAfter);

        Assert.Equal(4, d.KeyCountBefore);
        Assert.Equal(6, d.KeyCountAfter);
        Assert.Equal(["系統 › 新韌體", "螢幕與色域 › PANEL-1"], d.Added);
        Assert.Empty(d.Removed);
        // 主機名稱與作業系統變了——但只列鍵名，值（OLD-HOST／NEW-HOST）不得出現在輸出
        Assert.Equal(["系統 › 作業系統", "系統 › 主機名稱"], d.Changed);
        Assert.False(d.Identical);
    }

    [Fact]
    public void 鍵級全同_回報相同_產生時間變了也不多算()
    {
        // 產生時間是自由行（不是鍵值列），不進鍵表；同鍵同值才是「全同」
        var d = ReportDiffService.Compare(MdBefore, MdBefore.Replace("08:00:00", "10:00:00"));
        Assert.True(d.Identical);
    }

    [Fact]
    public void diff輸出不含任何值_敏感字串不得出現在變化清單()
    {
        var d = ReportDiffService.Compare(MdBefore, MdAfter);

        // 變化的鍵只有鍵名；新舊值（Windows 11／Windows 12／OLD-HOST／NEW-HOST）一個都不許出現
        Assert.All(d.Added.Concat(d.Removed).Concat(d.Changed), key =>
        {
            Assert.DoesNotContain("OLD-HOST", key);
            Assert.DoesNotContain("NEW-HOST", key);
            Assert.DoesNotContain("Windows", key);
            Assert.DoesNotContain("SERIAL", key);
        });
    }

    [Fact]
    public void 跨格式互比_MD對TXT_鍵的形狀一致()
    {
        // 與 ReportService.RenderText 同形：節與小標頂格、資料列兩格內縮、欄間隔兩格、表頭下有虛線
        var txt = """
            曦覽 XinSpect 硬體報告
            ============================================================
            產生時間　2026-10-25 08:00:00
            主機　　　OLD-HOST
            處理器　　Fake CPU


            【系統】
            ------------------------------------------------------------

              作業系統  Windows 11
              主機名稱  OLD-HOST
              磁碟序號  SERIAL-AAA

            已安裝模組

              插槽  製造商
              ----  --------
              DIMM_A1  ACME

            ---

            本報告由曦覽 XinSpect 在本機產生。
            """;
        var parsed = ReportDiffService.Parse(txt);

        Assert.Equal("text", parsed.Format);
        Assert.Contains("系統 › 作業系統", parsed.Keys);
        Assert.Equal("Windows 11", parsed.Keys["系統 › 作業系統"]);
        Assert.Contains("系統 › 主機名稱", parsed.Keys);
        Assert.Equal("OLD-HOST", parsed.Keys["系統 › 主機名稱"]);
        Assert.Contains("系統 › 已安裝模組 › DIMM_A1", parsed.Keys);

        // 與 Markdown 版互比：同一份中介表示產生的鍵必須對得上
        var d = ReportDiffService.Compare(MdBefore, txt);
        Assert.True(d.Identical, $"跨格式鍵表不一致：新增 {string.Join("、", d.Added)}；消失 {string.Join("、", d.Removed)}；變化 {string.Join("、", d.Changed)}");
    }

    [Fact]
    public void HTML版_表頭與小標不是鍵_資料列的值解碼後與MD一致()
    {
        var html = """
            <!DOCTYPE html><html lang="zh-Hant"><head><meta charset="utf-8">
            <title>曦覽 XinSpect 硬體報告</title></head><body><div class="wrap">
            <h2 id="system">系統</h2>
            <table class="kv"><tr><td>作業系統</td><td class="val">Windows &lt;11&gt;</td></tr>
            <tr><td>主機名稱</td><td class="val">OLD-HOST</td></tr></table>
            <div class="cap2">已安裝模組</div>
            <table><tr><th>插槽</th><th>製造商</th></tr><tr><td class="val">DIMM_A1</td><td>ACME</td></tr></table>
            </div></body></html>
            """;
        var parsed = ReportDiffService.Parse(html);

        Assert.Equal("html", parsed.Format);
        Assert.Equal("Windows <11>", parsed.Keys["系統 › 作業系統"]);     // 實體解碼
        Assert.Equal("OLD-HOST", parsed.Keys["系統 › 主機名稱"]);
        Assert.Contains("系統 › 已安裝模組 › DIMM_A1", parsed.Keys);     // 小標插在中間
        Assert.DoesNotContain(parsed.Keys.Keys, k => k.Contains("插槽")); // 表頭不是鍵
    }

    [Fact]
    public void 同名欄位在不同節_不互相冒充()
    {
        var a = ReportDiffService.Parse("""
            【系統】
              版本  24H2

            【螢幕與色域】
              版本  VESA 1.0
            """);
        Assert.Equal("24H2", a.Keys["系統 › 版本"]);
        Assert.Equal("VESA 1.0", a.Keys["螢幕與色域 › 版本"]);
        Assert.Equal(2, a.Keys.Count);
    }

    [Fact]
    public void 格式不認得_如實標unknown回空集_不硬解析()
    {
        var parsed = ReportDiffService.Parse("這是一份完全無關的文字檔。");
        Assert.Equal("unknown", parsed.Format);
        Assert.Empty(parsed.Keys);
    }

    [Fact]
    public void AI回覆節_自由文字不當鍵源()
    {
        var txt = """
            【AI 評價】

              ※ 以下是 AI 分頁最後一次的回覆……

                這段是 AI 的自由文字，縮排的行看起來像資料列：
                溫度  90 度   ← 這是 AI 說的話，不是報告的鍵值對
            """;
        var parsed = ReportDiffService.Parse(txt);
        Assert.DoesNotContain(parsed.Keys.Keys, k => k.Contains("溫度"));
    }

    [Fact]
    public void CLI_退出碼語意_全同0有差異2引數錯1()
    {
        string dir = Path.Combine(Path.GetTempPath(), "repdiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.md");
        string b = Path.Combine(dir, "b.md");
        File.WriteAllText(a, MdBefore, new UTF8Encoding(false));
        File.WriteAllText(b, MdAfter, new UTF8Encoding(false));

        // 有差異 → 2
        Assert.Equal(CliService.ExitPartial, CliService.Run(
            [CliService.DiffReportArg, a, b], () => [], new StringWriter(), new StringWriter()));
        // 全同 → 0
        Assert.Equal(CliService.ExitOk, CliService.Run(
            [CliService.DiffReportArg, a, a], () => [], new StringWriter(), new StringWriter()));
        // 引數錯 → 1
        Assert.Equal(CliService.ExitError, CliService.Run(
            [CliService.DiffReportArg, a], () => [], new StringWriter(), new StringWriter()));
        // 讀檔失敗 → 1
        Assert.Equal(CliService.ExitError, CliService.Run(
            [CliService.DiffReportArg, a, Path.Combine(dir, "不存在.md")], () => [], new StringWriter(), new StringWriter()));

        Directory.Delete(dir, true);
    }
}
