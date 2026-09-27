using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 整機驗機報告的收攏與排序。純函式,餵合成 finding 即可驗;不碰硬體。
/// </summary>
public class MachineVerdictTests
{
    private static readonly DateTime T = DateTime.UnixEpoch;

    private static VerifyFact Ev(string label, string value) =>
        new(FactId.DimmCount, label, value, null, "", FactSource.Smbios, "SMBIOS Type 17", false,
            FactTrust.FirmwareReported, T);

    private static VerdictLine Line(string scope, string id, string part, VerifyVerdict v,
        string? benign = null, VerifyFact[]? ev = null) =>
        new(scope, new VerifyFinding(id, part, id + " 標題", v,
            v == VerifyVerdict.Conflict ? Severity.Warning : v == VerifyVerdict.Match ? Severity.Good : Severity.Neutral,
            "說明文字", benign, ev ?? []));

    [Fact]
    public void 排序_矛盾在最前_讀不到次之_相符最後()
    {
        var v = MachineVerdictBuilder.Build(T,
        [
            Line("整機", "R-A", "記憶體", VerifyVerdict.Match),
            Line("碟0", "R-B", "儲存裝置", VerifyVerdict.Unread),
            Line("碟0", "R-C", "儲存裝置", VerifyVerdict.Conflict),
        ]);

        Assert.Equal(VerifyVerdict.Conflict, v.Lines[0].Finding.Verdict);
        Assert.Equal(VerifyVerdict.Unread, v.Lines[1].Finding.Verdict);
        Assert.Equal(VerifyVerdict.Match, v.Lines[2].Finding.Verdict);
        Assert.Equal(1, v.Conflict);
        Assert.Equal(1, v.Unread);
        Assert.Equal(1, v.Match);
        Assert.Equal(3, v.Total);
    }

    [Fact]
    public void 總結_不含分數也不含正品字眼()
    {
        var v = MachineVerdictBuilder.Build(T, [Line("整機", "R-A", "記憶體", VerifyVerdict.Match)]);
        Assert.DoesNotContain("分", v.Summary);
        Assert.DoesNotContain("正品", v.Summary);
        Assert.DoesNotContain("翻新", v.Summary);
    }

    [Fact]
    public void 總結_有矛盾時點出集中在哪些部件()
    {
        var v = MachineVerdictBuilder.Build(T,
        [
            Line("碟0", "R-C", "儲存裝置", VerifyVerdict.Conflict),
            Line("整機", "R-D", "電池", VerifyVerdict.Conflict),
            Line("整機", "R-A", "記憶體", VerifyVerdict.Match),
        ]);
        Assert.Contains("矛盾集中在", v.Summary);
        Assert.Contains("儲存裝置", v.Summary);
        Assert.Contains("電池", v.Summary);
    }

    [Fact]
    public void 總結_全相符時明說不代表機器一定沒問題()
    {
        var v = MachineVerdictBuilder.Build(T, [Line("整機", "R-A", "記憶體", VerifyVerdict.Match)]);
        Assert.Contains("不代表", v.Summary);
    }

    [Fact]
    public void 空清單_不丟例外_總結說沒有可對帳的事實()
    {
        var v = MachineVerdictBuilder.Build(T, []);
        Assert.Equal(0, v.Total);
        Assert.Contains("沒有可對帳", v.Summary);
    }

    [Fact]
    public void 純文字報告_矛盾附證據來源與正當成因()
    {
        var v = MachineVerdictBuilder.Build(T,
        [
            Line("碟0 ・ INTEL SSD", "R-SSD-01", "儲存裝置", VerifyVerdict.Conflict,
                benign: "長期影音錄製也會這樣", ev: [Ev("通電小時", "42"), Ev("累計寫入", "8700 GiB")]),
        ]);
        string text = MachineVerdictBuilder.ToPlainText(v);

        Assert.Contains("整機驗機報告", text);
        Assert.Contains("R-SSD-01", text);
        Assert.Contains("碟0 ・ INTEL SSD", text);
        Assert.Contains("通電小時=42", text);
        Assert.Contains("SMBIOS Type 17", text);          // 證據來源(讀取方法)有印出來
        Assert.Contains("長期影音錄製也會這樣", text);       // 正當成因有印出來
        Assert.Contains("不下「正品／翻新」結論", text);      // 誠實聲明
    }

    [Fact]
    public void 純文字報告_讀不到的項目也列出_不隱藏()
    {
        var v = MachineVerdictBuilder.Build(T, [Line("碟0", "R-SSD-05", "儲存裝置", VerifyVerdict.Unread)]);
        string text = MachineVerdictBuilder.ToPlainText(v);
        Assert.Contains("讀不到", text);
        Assert.Contains("R-SSD-05", text);
    }
}
