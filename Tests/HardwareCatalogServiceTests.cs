using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

public sealed class HardwareCatalogServiceTests
{
    [Fact]
    public void Search_filters_by_kind_and_query()
    {
        var catalog = CreateCatalog();
        var cpu = catalog.Search("Core 2 Duo", "CPU");
        Assert.NotEmpty(cpu);
        Assert.All(cpu, row => Assert.Equal("CPU", row.Kind));
        Assert.Contains(cpu, row => row.DisplayName.Contains("Core 2 Duo", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(catalog.Search("Core 2 Duo", "GPU"));
    }

    [Fact]
    public void Search_does_not_include_noncommercial_motherboard_data()
    {
        var catalog = CreateCatalog();
        Assert.Empty(catalog.Search("B760M-AYW"));
        Assert.Contains("CPU/GPU", catalog.Attribution, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("不是本機量測", catalog.Limitations);
    }

    [Fact]
    public void Search_can_return_more_than_eighty_rows()
    {
        var catalog = CreateCatalog();
        var rows = catalog.Search("", limit: null);
        Assert.True(rows.Count > 80);
    }

    [Fact]
    public void Gpu_fields_are_searchable_and_displayed()
    {
        var catalog = CreateCatalog();
        var rows = catalog.Search("G80", "GPU");
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.SearchText.Contains("G80", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row.Summary.Contains("G80", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_user_catalog_is_reported_without_shipping_asset()
    {
        var catalog = new HardwareCatalogService();
        Assert.Contains("尚未匯入", catalog.Status);
        Assert.Contains("不內建", catalog.Limitations);
    }

    private static HardwareCatalogService CreateCatalog()
    {
        var records = Enumerable.Range(0, 100).Select(i => new
        {
            kind = i == 99 ? "GPU" : "CPU",
            year = 2006,
            vendor = i == 99 ? "NVIDIA" : "Intel",
            model = i == 99 ? "GeForce 8800 GTX" : $"Core 2 Duo E{i:000}",
            family = i == 99 ? "GeForce 8" : "Core 2",
            segment = "桌上型",
            architecture = i == 99 ? "Tesla" : "Conroe",
            gpuChip = i == 99 ? "G80" : null,
            evidence = "A/B",
            status = "正式量產",
            notes = "測試資料",
            sourceUrl = "https://example.invalid/source",
            sourceRef = "TEST"
        }).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "xinspect-catalog-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            attribution = "測試用 CPU/GPU 資料；不是本機量測",
            limitations = "測試資料不內建；不是本機量測",
            records
        }));
        var catalog = new HardwareCatalogService();
        catalog.LoadFile(path);
        File.Delete(path);
        return catalog;
    }
}
