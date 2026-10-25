using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 快照 JSON schema 版本化的相容性契約（U10-PF-0004／exp.json.schema）：
/// 匯出一定帶 schemaVersion、驗證器對未知版本如實拒收、加法擴充的相容宣告在
/// 規格文件與程式兩邊成文且一致。文件是規格，不是裝飾——改一邊不跟另一邊就是紅燈。
/// </summary>
public class SnapshotSchemaCompatTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "spec")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string SchemaText() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "spec", "snapshot.schema.v1.json"));

    private static HardwareSnapshot SampleSnapshot(int version) => new()
    {
        SchemaVersion = version,
        AppVersion = "test",
        AnonymousMachineId = HardwareSnapshotService.CreateAnonymousMachineId(["MACHINE-PART"]),
        CapturedAtUtc = DateTimeOffset.Parse("2026-10-25T00:00:00+00:00"),
        SensitiveValuesPreserved = false,
        Facts =
        [
            new HardwareSnapshotFact
            {
                Key = "k", Category = "c", Name = "n", Value = "v", Source = "s",
                Trust = FactTrustLevel.Measured, Sensitive = false,
                MeasuredAtUtc = DateTimeOffset.Parse("2026-10-25T00:00:00+00:00"),
                Availability = FactAvailability.Present,
            },
        ],
        Integrity = new HardwareSnapshotIntegrity { Algorithm = "sha256", Hash = "h" },
    };

    [Fact]
    public void 匯出一定帶schemaVersion且與目前版本一致()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(SampleSnapshot(1), options));

        Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out var v), "匯出缺 schemaVersion 欄位");
        Assert.Equal(HardwareSnapshotSchema.CurrentVersion, v.GetInt32());
        Assert.Equal(1, HardwareSnapshotSchema.CurrentVersion);
    }

    [Fact]
    public void 未知版本如實拒收_不硬讀()
    {
        // 以「合法形狀、未知版本」的 JSON 直接落地：Load 應在驗證第一步就拒收，
        // 而不是硬讀到一半才炸或把未知版本當舊版讀。
        string path = Path.Combine(Path.GetTempPath(), $"xincompat-{Guid.NewGuid():N}.json");
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            };
            string json = JsonSerializer.Serialize(SampleSnapshot(1), options)
                .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
            Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
            File.WriteAllText(path, json);

            var ex = Assert.Throws<NotSupportedException>(() => HardwareSnapshotService.Load(path));
            Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        }
        finally { try { File.Delete(path); } catch { } }

        // 判決文字與 IsKnown 同一口徑：不認得就說拒收，並指出目前版本。
        Assert.False(SnapshotSchemaCompatService.IsKnown(2));
        Assert.False(SnapshotSchemaCompatService.IsKnown(0));
        Assert.Contains("拒收", SnapshotSchemaCompatService.Describe(2), StringComparison.Ordinal);
        Assert.Contains("不支援", SnapshotSchemaCompatService.Describe(2), StringComparison.Ordinal);
        Assert.True(SnapshotSchemaCompatService.IsKnown(1));
        Assert.Contains("支援", SnapshotSchemaCompatService.Describe(1), StringComparison.Ordinal);
    }

    [Fact]
    public void 加法擴充的相容宣告_文件與程式兩邊成文且一致()
    {
        string schema = SchemaText();

        // 文件側：版本化相容性規則要寫進規格文件（description 成文）。
        Assert.Contains("schemaVersion", schema, StringComparison.Ordinal);
        Assert.Contains("加法擴充", schema, StringComparison.Ordinal);
        Assert.Contains("升版", schema, StringComparison.Ordinal);
        Assert.Contains("如實拒收", schema, StringComparison.Ordinal);

        // 程式側：同一套規則在服務裡成文，關鍵概念兩邊都有。
        var rules = SnapshotSchemaCompatService.CompatibilityRules;
        Assert.True(rules.Count >= 4, "相容性規則少於四條——版本承諾沒有寫全");
        string joined = string.Join("\n", rules);
        Assert.Contains("加法擴充", joined, StringComparison.Ordinal);
        Assert.Contains("升版", joined, StringComparison.Ordinal);
        Assert.Contains("拒收", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void 加法擴充的先例成立_Unknown在文件枚舉內且註明相容性代價()
    {
        // 六態的 Unknown 是「v1 之內加枚舉值」的既有先例：文件把這個先例與它的代價
        //（舊讀者會拒收新值）一起成文——相容宣告不是空話，是對已發生過的擴充如實記帳。
        using var doc = JsonDocument.Parse(SchemaText());
        var availability = doc.RootElement.GetProperty("$defs").GetProperty("fact")
            .GetProperty("properties").GetProperty("availability");
        var values = availability.GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet();
        Assert.Contains("Unknown", values);

        string note = availability.GetProperty("description").GetString() ?? "";
        Assert.Contains("加法擴充", note, StringComparison.Ordinal);
        Assert.Contains("拒收", note, StringComparison.Ordinal);
    }
}
