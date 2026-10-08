using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 記憶體 ECC／Registered／平台更正能力的三層分離（純函式）。
/// 這一組測試存在的理由：這三者是最常被混成一句「這是伺服器記憶體」的三個獨立屬性，
/// 而它們在 SMBIOS 裡是三組不同的欄位——混著講就會把「ECC UDIMM 工作站」說成伺服器。
/// </summary>
public class DimmEccJudgeTests
{
    // ── 第一層：模組有沒有 ECC 位元（看匯流排寬度） ────────────────────────

    [Fact]
    public void 總寬度大於資料寬度_模組帶ECC位元()
    {
        // 64 資料 + 8 ECC = 72（本機實測的 TotalWidth 就是 72）
        Assert.True(DimmEccJudge.HasEcc(new DimmBusWidth(64, 72)));
    }

    [Fact]
    public void 總寬度等於資料寬度_模組無ECC位元()
    {
        Assert.False(DimmEccJudge.HasEcc(new DimmBusWidth(64, 64)));
    }

    [Fact]
    public void 寬度讀不到_回null而不是沒有ECC()
    {
        // 0 與 0xFFFF 都是「未知」。當成「沒有 ECC」是把未知講成已知。
        Assert.Null(DimmEccJudge.HasEcc(new DimmBusWidth(0, 0)));
        Assert.Null(DimmEccJudge.HasEcc(new DimmBusWidth(64, 0xFFFF)));
        Assert.Null(DimmEccJudge.HasEcc(new DimmBusWidth(0xFFFF, 0xFFFF)));
    }

    [Fact]
    public void 非常見寬度_只要總寬度較大就算有ECC位元()
    {
        // 32 位元資料 + 7 位元 ECC = 39（較舊的 SODIMM ECC 形式）
        Assert.True(DimmEccJudge.HasEcc(new DimmBusWidth(32, 39)));
    }

    // ── 第二層：模組型態（Registered 欄位） ───────────────────────────────

    [Theory]
    [InlineData(0x03, DimmEccJudge.ModuleForm.Registered)]
    [InlineData(0x04, DimmEccJudge.ModuleForm.Unbuffered)]
    [InlineData(0x00, DimmEccJudge.ModuleForm.Unknown)]
    [InlineData(0x01, DimmEccJudge.ModuleForm.Unknown)]
    [InlineData(0x02, DimmEccJudge.ModuleForm.Unknown)]
    [InlineData(0xFF, DimmEccJudge.ModuleForm.Unknown)]
    public void Registered欄位_只認規格明訂的兩個值(byte field, DimmEccJudge.ModuleForm expected)
        => Assert.Equal(expected, DimmEccJudge.FormOf(field));

    [Fact]
    public void 型態名稱_要看得出RDIMM與UDIMM的分別()
    {
        Assert.Contains("RDIMM", DimmEccJudge.FormName(DimmEccJudge.ModuleForm.Registered));
        Assert.Contains("UDIMM", DimmEccJudge.FormName(DimmEccJudge.ModuleForm.Unbuffered));
        Assert.Contains("未回報", DimmEccJudge.FormName(DimmEccJudge.ModuleForm.Unknown));
    }

    // ── 第三層：平台錯誤更正能力 ──────────────────────────────────────────

    [Theory]
    [InlineData(0x03, "無")]
    [InlineData(0x05, "單位元 ECC")]
    [InlineData(0x06, "多位元 ECC")]
    [InlineData(0x07, "CRC")]
    [InlineData(0x04, "同位")]
    public void 平台更正能力(byte code, string expected)
        => Assert.Equal(expected, DimmEccJudge.PlatformEccName(code));

    // ── 三層合起來：不得互相推論 ──────────────────────────────────────────

    [Fact]
    public void 敘述_三層各自陳述且明說不互相推論()
    {
        string text = DimmEccJudge.Describe(
            moduleHasEcc: true, form: DimmEccJudge.ModuleForm.Unbuffered, platformEcc: "無");

        Assert.Contains("模組帶 ECC 位元", text);
        Assert.Contains("UDIMM", text);
        Assert.Contains("不從其中一項推論另一項", text);
    }

    [Fact]
    public void 敘述_模組有ECC但平台說無_要指出不一致()
    {
        // 這是最需要提醒的組合：兩個欄位對不上，不能靜靜挑一個講
        string text = DimmEccJudge.Describe(true, DimmEccJudge.ModuleForm.Unbuffered, "無");
        Assert.Contains("不一致", text);
        Assert.Contains("以平台宣告為準", text);
    }

    [Fact]
    public void 敘述_平台有ECC但模組沒有_也要指出不一致()
    {
        string text = DimmEccJudge.Describe(false, DimmEccJudge.ModuleForm.Unbuffered, "單位元 ECC");
        Assert.Contains("不一致", text);
    }

    [Fact]
    public void 敘述_讀不到時如實標未知且不編結論()
    {
        string text = DimmEccJudge.Describe(null, DimmEccJudge.ModuleForm.Unknown, null);

        Assert.Contains("讀不到", text);
        Assert.Contains("無法判斷", text);
        // 不得出現「模組無 ECC 位元」這種把未知講成已知的句子
        //（注意：「無法判斷有無 ECC」本身含有「無 ECC」字樣，故比對完整片語而非子字串）
        Assert.DoesNotContain("模組無 ECC 位元", text);
        Assert.DoesNotContain("模組帶 ECC 位元", text);
    }

    [Fact]
    public void 敘述_不一致的提醒不得在相符時出現()
    {
        string ok = DimmEccJudge.Describe(true, DimmEccJudge.ModuleForm.Registered, "多位元 ECC");
        Assert.DoesNotContain("不一致", ok);
    }
}
