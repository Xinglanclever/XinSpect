using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP47 公開規格的機器對帳：docs/spec/ 下的規格不是裝飾文件——
/// 快照 JSON Schema 的屬性集必須與實際序列化形狀「逐鍵相等」（枚舉清單也要對上），
/// 查詢語言規格必須涵蓋 QueryParser 全部頁鍵與別名，方法學／限制／認證骨架必須存在且講誠實契約。
/// 文件漂移＝紅燈，跟對帳規則同一套哲學。
/// </summary>
public class PublicSpecTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "spec")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Spec(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "spec", file));

    [Fact]
    public void 快照Schema_v1_與實際序列化形狀逐鍵相等()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };
        var snapshot = new HardwareSnapshot
        {
            SchemaVersion = 1,
            AppVersion = "test",
            AnonymousMachineId = "abc",
            CapturedAtUtc = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"),
            SensitiveValuesPreserved = false,
            Facts =
            [
                new HardwareSnapshotFact
                {
                    Key = "k", Category = "c", Name = "n", Value = "v",
                    Source = "s", Trust = FactTrustLevel.Measured, Sensitive = false,
                    MeasuredAtUtc = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"),
                    Availability = FactAvailability.Present,
                },
            ],
            Integrity = new HardwareSnapshotIntegrity { Algorithm = "sha256", Hash = "h" },
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, options));
        using var schema = JsonDocument.Parse(Spec("snapshot.schema.v1.json"));

        var schemaProps = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToHashSet();
        var actualKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(schemaProps, actualKeys);

        var factSchema = schema.RootElement.GetProperty("$defs").GetProperty("fact");
        var factProps = factSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        var factKeys = doc.RootElement.GetProperty("facts")[0].EnumerateObject().Select(p => p.Name).ToHashSet();
        // numericValue／unit 有條件忽略（null／預設值整鍵省略）——實際鍵必為 schema 屬性的子集
        Assert.Subset(factProps, factKeys);

        var trustValues = factSchema.GetProperty("properties").GetProperty("trust")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(Enum.GetNames<FactTrustLevel>().ToHashSet(), trustValues);
        var availValues = factSchema.GetProperty("properties").GetProperty("availability")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(Enum.GetNames<FactAvailability>().ToHashSet(), availValues);
    }

    [Fact]
    public void 查詢語言規格_涵蓋解析器全部頁鍵與別名()
    {
        string spec = Spec("query-language.v1.md");
        Assert.All(QueryParser.ValidFields, field => Assert.Contains($"`{field}`", spec));
        Assert.Contains("`~`", spec);
        Assert.Contains("`^`", spec);
        Assert.Contains("no-permission", spec);   // availability 別名要寫進規格
        Assert.Contains("查不到≠沒有", spec);      // 核心語意必須成文
    }

    [Fact]
    public void 方學與限制與認證骨架_存在且講誠實契約()
    {
        string methodology = Spec("METHODOLOGY.md");
        Assert.Contains("三態", methodology);
        Assert.Contains("SpecRef", methodology);

        string limitations = Spec("LIMITATIONS.md");
        Assert.Contains("EC", limitations);
        Assert.Contains("Rowhammer", limitations);
        Assert.Contains("IPMI", limitations);

        string cert = Spec("CERTIFICATION-PLAN.md");
        Assert.Contains("認證", cert);
        Assert.Contains("骨架", cert);            // 明示這只是骨架，不是已執行的認證
    }
}
