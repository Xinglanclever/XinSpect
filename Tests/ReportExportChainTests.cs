using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 匯出檔內嵌雜湊鏈（U10-PF-0009／<c>exp.hash.chain</c>）的守門：簽→驗往返、鏈首陳述、
/// 跨檔串接、竄改一位元組即失敗、帳本上的「前份」取用。
/// 全部走暫存目錄與純函式，<b>不碰</b> %ProgramData% 的真帳本（<see cref="ReportExportChainService.SignAndRecord"/>
/// 的帳本接線在 UI 測試的職責範圍外，這裡只驗證它依賴的純核心）。
/// </summary>
public class ReportExportChainTests
{
    [Fact]
    public void 簽了就能驗_往返不失真()
    {
        // 兩種內容形狀都試：帶尾換行與不帶——Sign 會正規化成以換行收尾再取雜湊
        foreach (string content in new[] { "列1,列2,列3\n", "<html><body>報告</body></html>" })
        {
            string signed = ReportExportChainService.Sign(content, previousHash: null);
            var v = ReportExportChainService.VerifyText(signed);
            Assert.True(v.Valid, v.FailureReason);
            Assert.Equal(ReportExportChainService.GenesisLabel, v.PreviousHash);
            Assert.NotNull(v.ContentHash);
            // 簽章行蓋的是「簽章行之前（含其前一個換行）」的位元組
            string normalized = content.EndsWith('\n') ? content : content + "\n";
            Assert.Equal(ReportExportChainService.ContentHashOf(normalized), v.ContentHash);
        }
    }

    [Fact]
    public void 無前份_如實寫鏈首()
    {
        string signed = ReportExportChainService.Sign("內容\n", previousHash: null);
        Assert.Contains($"previous={ReportExportChainService.GenesisLabel}", signed);
        var v = ReportExportChainService.VerifyText(signed);
        Assert.True(v.Valid);
        Assert.Equal(ReportExportChainService.GenesisLabel, v.PreviousHash);
    }

    [Fact]
    public void 兩次匯出_第二份帶前份雜湊_兩份都驗得過()
    {
        string e1 = ReportExportChainService.Sign("第一份\n", previousHash: null);
        var v1 = ReportExportChainService.VerifyText(e1);
        Assert.True(v1.Valid, v1.FailureReason);

        // 第二次匯出：前份＝第一份的內容雜湊（帳本接線在 SignAndRecord，這裡手動串）
        string e2 = ReportExportChainService.Sign("第二份，內容不同\n", v1.ContentHash);
        var v2 = ReportExportChainService.VerifyText(e2);
        Assert.True(v2.Valid, v2.FailureReason);
        Assert.Equal(v1.ContentHash, v2.PreviousHash);   // 鏈串起來了

        // 寫成檔案再驗：鏈的驗證入口是讀回檔案→重算→比對
        string path = Path.Combine(Path.GetTempPath(), "expchain-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllText(path, e2);
            Assert.True(ReportExportChainService.VerifyFile(path).Valid);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 竄改一個位元組_驗證失敗()
    {
        string signed = ReportExportChainService.Sign("A,B,C\n1,2,3\n", previousHash: null);

        // 內容區改一個字元
        var tampered = signed.Replace("1,2,3", "1,2,4");
        Assert.NotEqual(signed, tampered);
        var v = ReportExportChainService.VerifyText(tampered);
        Assert.False(v.Valid);
        Assert.Contains("內容雜湊不符", v.FailureReason);

        // 連簽章行本體改一個字元（重算也對不上）也失敗
        string forgedTail = signed.Replace("algorithm=sha256", "algorithm=sha255");
        Assert.False(ReportExportChainService.VerifyText(forgedTail).Valid);
    }

    [Fact]
    public void 沒有簽章行的檔案_如實說不是鏈上的一環()
    {
        var v = ReportExportChainService.VerifyText("舊版匯出的檔案，沒有簽章\n");
        Assert.False(v.Valid);
        Assert.Contains(ReportExportChainService.ChainKey, v.FailureReason);
    }

    [Fact]
    public void 帳本上前份_取最後一筆匯出紀錄_其他動作不干擾()
    {
        var entries = new System.Collections.Generic.List<AuditEntry>();
        void Add(string action, string hash) =>
            entries.Add(AuditLogService.Append(entries, "tester", "", action, "f.csv", "n", hash, DateTimeOffset.UtcNow));

        Assert.Null(ReportExportChainService.PreviousFromLedger(entries));   // 空帳＝鏈首

        Add(ReportExportChainService.ExportAction, "hash-first");
        Add("knowledge-pack-swap", "hash-other");     // 中間夾了別種動作
        Add(ReportExportChainService.ExportAction, "hash-last");
        Assert.Equal("hash-last", ReportExportChainService.PreviousFromLedger(entries));
    }
}
