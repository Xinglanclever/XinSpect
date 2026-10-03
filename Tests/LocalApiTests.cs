using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP34＋WP35 的契約：本機 API 的請求處理（純函式，HTTP 殼層極薄不在單測範圍）。
/// 只綁 loopback、唯讀、匿名機器識別——GET /api/facts 回全部事實、POST /api/query 吃
/// 查詢語言全文（WP35：CLI 與 API 同一語法）、未知路徑 404 帶說明。
/// </summary>
public class LocalApiTests
{
    private static HardwareSnapshot Snapshot()
    {
        var at = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00");
        HardwareSnapshotFact F(string key, string value) => new()
        {
            Key = key, Category = "c", Name = key, Value = value,
            Source = "s", Trust = FactTrustLevel.Reported, Sensitive = false,
            MeasuredAtUtc = at,
        };
        return new HardwareSnapshot
        {
            SchemaVersion = 1, AppVersion = "t", AnonymousMachineId = "sha256:" + new string('a', 64),
            CapturedAtUtc = at, SensitiveValuesPreserved = false,
            Facts = [F("spi.hsfsts", "FLOCKDN=1"), F("msr.0x8b", "0x02007006")],
            Integrity = new HardwareSnapshotIntegrity { Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm, Hash = new string('a', 64) },
        };
    }

    [Fact]
    public void API_事實端點回JSON與查詢端點吃查詢語言()
    {
        var snap = Snapshot();
        Func<HardwareSnapshot?> current = () => snap;

        var (status1, json1) = XinSpect.LocalApiHandler.Handle("GET", "/api/facts", null, current);
        Assert.Equal(200, status1);
        Assert.Contains("spi.hsfsts", json1);
        Assert.Contains("msr.0x8b", json1);

        // WP35：查詢語言全文（一行一子句），不是前綴
        var (status2, json2) = XinSpect.LocalApiHandler.Handle("POST", "/api/query",
            "key ^= msr.\navailability = ok", current);
        Assert.Equal(200, status2);
        Assert.Contains("msr.0x8b", json2);
        Assert.DoesNotContain("spi.hsfsts", json2);

        // 查詢語法錯誤＝400 帶修正指引，不是 200 空結果
        var (status3, json3) = XinSpect.LocalApiHandler.Handle("POST", "/api/query", "nonsense", current);
        Assert.Equal(400, status3);
        Assert.Contains("ParseException", json3);
    }

    [Fact]
    public void API_未知路徑與無快照與舊前綴相容()
    {
        var (status404, json404) = XinSpect.LocalApiHandler.Handle("GET", "/api/nothing", null, Snapshot);
        Assert.Equal(404, status404);
        Assert.Contains("/api/facts", json404);

        var (status503, _) = XinSpect.LocalApiHandler.Handle("GET", "/api/facts", null, () => null);
        Assert.Equal(503, status503);

        // 舊前綴語意相容：值不含 = 時當 key 前綴
        var (status4, json4) = XinSpect.LocalApiHandler.Handle("POST", "/api/query", "spi.", Snapshot);
        Assert.Equal(200, status4);
        Assert.Contains("spi.hsfsts", json4);
        Assert.DoesNotContain("msr.0x8b", json4);
    }
}
