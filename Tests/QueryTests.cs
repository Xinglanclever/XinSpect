using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// A44 查詢語言的契約：解析（合法/非法欄位、運算子、日期、註解、一行一子句值可含空格）、
/// 執行（篩選管線、三態區分「沒有這個事實」與「存在但讀不到」、時間軸、跨機器）。
/// 絕不猜：未知輸入直接丟 QueryParseException。
/// </summary>
public class QueryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static QueryFact F(string key, string value = "v", string category = "韌體安全",
        string source = "測試來源", FactAvailability availability = FactAvailability.Present,
        string? reason = null, DateTimeOffset? measured = null, string? machine = null) =>
        new(key, category, source, value, availability, reason, measured ?? At, machine, machine is null ? null : At);

    // ===== 解析 =====

    [Fact]
    public void 解析_多行管線與註解與含空格的值()
    {
        var q = QueryParser.Parse("""
            # 只看韌體安全裡讀不到的
            category=韌體安全
            availability=read-error
            key^=spi.
            source~=PCI 設定空間 0x00
            """);

        Assert.Equal(4, q.Clauses.Count);
        Assert.Equal(("category", QueryOperator.Equals, "韌體安全"), (q.Clauses[0].Field, q.Clauses[0].Op, q.Clauses[0].Value));
        Assert.Equal(("availability", QueryOperator.Equals, "read-error"), (q.Clauses[1].Field, q.Clauses[1].Op, q.Clauses[1].Value));
        Assert.Equal(("key", QueryOperator.StartsWith, "spi."), (q.Clauses[2].Field, q.Clauses[2].Op, q.Clauses[2].Value));
        Assert.Equal("PCI 設定空間 0x00", q.Clauses[3].Value); // 含空格的值吃到行尾
    }

    [Fact]
    public void 解析_未知欄位與壞日期與缺值都丟ParseException()
    {
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("typo=x"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("沒有運算子"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("since=不是日期"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("availability=大概吧"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("key="));

        var ex = Assert.Throws<QueryParseException>(() => QueryParser.Parse("typo=x"));
        Assert.Contains("合法欄位", ex.Message); // 訊息要教使用者怎麼改
    }

    [Fact]
    public void 可用性別名_列舉名與常用詞都收()
    {
        Assert.Equal(FactAvailability.Present, QueryParser.ParseAvailability("present"));
        Assert.Equal(FactAvailability.Present, QueryParser.ParseAvailability("ok"));
        Assert.Equal(FactAvailability.ReadError, QueryParser.ParseAvailability("read-error"));
        Assert.Equal(FactAvailability.InsufficientPrivilege, QueryParser.ParseAvailability("no-permission"));
        Assert.Equal(FactAvailability.NotSupported, QueryParser.ParseAvailability("Not-Supported")); // 大小寫不拘
        Assert.Null(QueryParser.ParseAvailability("大概吧"));
    }

    // ===== 執行 =====

    [Fact]
    public void 執行_管線逐層篩選()
    {
        var q = QueryParser.Parse("category=韌體安全\nkey^=spi.\navailability=read-error");
        var facts = new[]
        {
            F("spi.hsfsts", "已鎖定", availability: FactAvailability.ReadError, reason: "MMIO 失敗"),
            F("spi.frap", "可寫入"),
            F("chipset.bios_cntl", "最強保護", availability: FactAvailability.ReadError, reason: "ring0 缺"),
        };
        var result = QueryExecutor.Run(q, facts);

        var m = Assert.Single(result.Matches);
        Assert.Equal("spi.hsfsts", m.Key);
        Assert.Equal(3, result.Scanned);
    }

    [Fact]
    public void 查不到不等於沒有_三態項是正常匹配_摘要明說區別()
    {
        // 查 spi.hsfsts：它存在但讀不到——必須回匹配（帶原因），不是「查無」
        var q = QueryParser.Parse("key=spi.hsfsts");
        var result = QueryExecutor.Run(q, [F("spi.hsfsts", "", availability: FactAvailability.ReadError, reason: "MMIO 失敗")]);
        var m = Assert.Single(result.Matches);
        Assert.Equal(FactAvailability.ReadError, m.Availability);
        Assert.Contains("MMIO 失敗", m.UnavailableReason);
        Assert.Contains("1 筆讀不到——三態原因隨附，不是沒有這個事實", result.SummaryText);

        // 查不存在的鍵：0 筆，摘要說可能沒有這個事實
        var none = QueryExecutor.Run(QueryParser.Parse("key=不存在的鍵"), [F("spi.hsfsts", "x")]);
        Assert.Empty(none.Matches);
        Assert.Contains("可能沒有這個事實", none.SummaryText);
    }

    [Fact]
    public void 時間軸查詢_since與until夾出範圍()
    {
        var q = QueryParser.Parse("since=2026-10-01\nuntil=2026-10-02");
        var facts = new[]
        {
            F("a", "v", measured: new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)),
            F("b", "v", measured: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
            F("c", "v", measured: new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero)),
        };
        var matches = QueryExecutor.Run(q, facts).Matches;
        Assert.Single(matches);
        Assert.Equal("b", matches[0].Key);
    }

    [Fact]
    public void 跨機器_兩份快照同查_結果帶匿名機器識別()
    {
        var q = QueryParser.Parse("key^=chipset.");
        var snapA = new HardwareSnapshot
        {
            AppVersion = "2.1.0", AnonymousMachineId = "機器A雜湊", CapturedAtUtc = At,
            Facts = [new HardwareSnapshotFact { Key = "chipset.bios_cntl", Category = "韌體安全", Name = "BIOS 寫入保護", Value = "最強保護", Source = "s", Trust = FactTrustLevel.Measured, MeasuredAtUtc = At }],
            Integrity = new HardwareSnapshotIntegrity { Algorithm = "SHA-256", Hash = "x" },
        };
        var snapB = new HardwareSnapshot
        {
            AppVersion = "2.1.0", AnonymousMachineId = "機器B雜湊", CapturedAtUtc = At,
            Facts = [new HardwareSnapshotFact { Key = "chipset.smramc", Category = "韌體安全", Name = "SMRAM 鎖定", Value = "已鎖", Source = "s", Trust = FactTrustLevel.Measured, MeasuredAtUtc = At }],
            Integrity = new HardwareSnapshotIntegrity { Algorithm = "SHA-256", Hash = "y" },
        };

        var matches = QueryExecutor.RunOnSnapshots(q, [snapA, snapB]).Matches;
        Assert.Equal(2, matches.Count);
        Assert.Equal("機器A雜湊", Assert.Single(matches, m => m.Key == "chipset.bios_cntl").MachineId);
        Assert.Equal("機器B雜湊", Assert.Single(matches, m => m.Key == "chipset.smramc").MachineId);
    }

    [Fact]
    public void 快照轉換_三態與時間隨附()
    {
        var snap = new HardwareSnapshot
        {
            AppVersion = "2.1.0", AnonymousMachineId = "m", CapturedAtUtc = At,
            Facts = [new HardwareSnapshotFact { Key = "k", Category = "c", Name = "n", Value = "", Source = "s", Trust = FactTrustLevel.Unknown, MeasuredAtUtc = At, Availability = FactAvailability.InsufficientPrivilege, UnavailableReason = "缺 ring0" }],
            Integrity = new HardwareSnapshotIntegrity { Algorithm = "SHA-256", Hash = "x" },
        };
        var m = Assert.Single(QueryExecutor.RunOnSnapshot(QueryParser.Parse("key=k"), snap).Matches);
        Assert.Equal(FactAvailability.InsufficientPrivilege, m.Availability);
        Assert.Equal("缺 ring0", m.UnavailableReason);
        Assert.Equal(At, m.CapturedAtUtc);
    }
}
