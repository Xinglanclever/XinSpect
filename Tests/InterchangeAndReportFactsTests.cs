using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 推進第 123–132 項（外部介面＋報告輸出）的守門：全部用合成 fixture，不碰網路。
/// 紅線：webhook／REST 明文不做；匿名範本不得帶路徑、序號或敏感值；xlsx 與 SVG 不假裝值讀得到。
/// </summary>
public class InterchangeAndReportFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 22, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value, bool sensitive = false,
        FactAvailability availability = FactAvailability.Present, string reason = "")
        => new(key, "測試域", key, value, "", "測試", FactTrustLevel.Reported, sensitive, At, null,
            availability, availability == FactAvailability.Present ? null : reason);

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xininterchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── XT-001 C# 常數 ─────────────────────────────────────────────────────

    [Fact]
    public void CSharp常數_鍵轉PascalCase_字串仍保留原鍵()
    {
        string text = InterchangeAndReportFactsService.GenerateCSharpConstants(
            ["cpu.name", "acpi.bert.region_length", "net.listen.count"]);

        Assert.Contains("public const string KeyCpuName = \"cpu.name\";", text);
        Assert.Contains("public const string KeyAcpiBertRegionLength = \"acpi.bert.region_length\";", text);
        Assert.Contains("public const string KeyNetListenCount = \"net.listen.count\";", text);
        Assert.Contains("不是硬體值", text);
    }

    [Fact]
    public void CSharp常數_重複鍵會編號避免同名宣告()
    {
        string text = InterchangeAndReportFactsService.GenerateCSharpConstants(
            ["x", "x", "x!"]);

        Assert.Contains("KeyX = \"x\";", text);
        Assert.Contains("KeyX2 = \"x\";", text);
        Assert.Contains("KeyX3 = \"x!\";", text);
    }

    [Fact]
    public void CSharp常數_引號與反斜線會跳脫()
    {
        string text = InterchangeAndReportFactsService.GenerateCSharpConstants(["q\"x", "back\\slash"]);
        Assert.Contains("\"q\\\"x\"", text);
        Assert.Contains("\"back\\\\slash\"", text);
    }

    // ── XT-002 Python reader ───────────────────────────────────────────────

    [Fact]
    public void Python範例_不包含任何網路請求()
    {
        string text = InterchangeAndReportFactsService.GeneratePythonReaderSample();

        Assert.Contains("import json", text);
        Assert.Contains("schemaVersion " + HardwareSnapshotSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture), text);
        Assert.DoesNotContain("requests", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("urllib", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("socket", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Python範例_產生的語法可解析()
    {
        string text = InterchangeAndReportFactsService.GeneratePythonReaderSample();
        string path = Path.Combine(TempDir(), "reader.py");
        File.WriteAllText(path, text);
        var psi = new System.Diagnostics.ProcessStartInfo("python", "-m py_compile \"" + path + "\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    // ── XT-004 本機檔案觸發 ────────────────────────────────────────────────

    [Fact]
    public void 檔案觸發_缺檔是沒有觸發_不是空token()
    {
        string dir = TempDir();
        try
        {
            Assert.Null(InterchangeAndReportFactsService.ReadTriggerToken(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 檔案觸發_存在時有token且自動去邊空白()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".xinspect-trigger"), " snapshot \n");
            Assert.Equal("snapshot", InterchangeAndReportFactsService.ReadTriggerToken(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 檔案觸發_目錄不存在會講清楚_不靜默建立()
    {
        string missing = Path.Combine(TempDir(), "not-exists");
        DirectoryNotFoundException ex = Assert.Throws<DirectoryNotFoundException>(() =>
            InterchangeAndReportFactsService.ReadTriggerToken(missing));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public void 檔案觸發_可用假watcher只測接縫_不真的跑OS事件()
    {
        using var watcher = new InterchangeAndReportFactsService.LocalFileTriggerWatcher(TempDir());
        Assert.False(watcher.CurrentTriggerPresent);
        var called = new List<string>();
        watcher.Triggered += called.Add;
        watcher.TestFire(".xinspect-trigger");
        Assert.Equal([".xinspect-trigger"], called);
    }

    // ── XT-005 快照檔案整合 ────────────────────────────────────────────────

    [Fact]
    public async Task 快照整合_可讀取摘要_完整性通過才回()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "snapshot.json");
        var facts = new[] { new HardwareSnapshotFact { Key = "cpu.name", Category = "CPU", Name = "CPU", Value = "Test", Source = "s", MeasuredAtUtc = At } };
        var snapshot = HardwareSnapshotService.Create(facts, ["test-machine"], At, "test");
        await HardwareSnapshotService.SaveAsync(path, snapshot);
        try
        {
            string summary = InterchangeAndReportFactsService.ReadSnapshotSummary(path);
            Assert.Contains("schemaVersion=1", summary);
            Assert.Contains("facts=1", summary);
            Assert.Contains("redacted=true", summary);
            Assert.Contains("capturedUtc=", summary);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 快照整合_改檔完整性驗證失敗_ReadError不猜內容()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "snapshot.json");
        var facts = new[] { new HardwareSnapshotFact { Key = "cpu.name", Category = "CPU", Name = "CPU", Value = "Test", Source = "s", MeasuredAtUtc = At } };
        var snapshot = HardwareSnapshotService.Create(facts, ["test-machine"], At, "test");
        HardwareSnapshotService.Save(path, snapshot);
        File.WriteAllText(path, File.ReadAllText(path).Replace("Test", "Tampered"));
        try
        {
            var f = InterchangeAndReportFactsService.SnapshotFact(At, path);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("完整性不符", f.UnavailableReason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 快照整合_沒有快照時保持描述值_不冒充讀到了()
    {
        var f = InterchangeAndReportFactsService.SnapshotFact(At);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("摘要", f.Value);
        Assert.Contains("檔案整合", f.Name);
    }

    // ── XT-006 schema registry ─────────────────────────────────────────────

    [Fact]
    public void SchemaRegistry_包含靜態與動態家族_不偽稱雲端()
    {
        string json = InterchangeAndReportFactsService.GenerateSchemaRegistryJson();
        var doc = JsonDocument.Parse(json);

        Assert.Equal(FactKeyCatalog.Count, doc.RootElement.GetProperty("staticKeyCount").GetInt32());
        Assert.Equal(FactKeyCatalog.Count, doc.RootElement.GetProperty("staticKeys").GetArrayLength());
        Assert.Equal(FactKeyDynamicCatalog.Families.Count, doc.RootElement.GetProperty("dynamicFamilies").EnumerateObject().Count());
        Assert.Contains("離線格式目錄", doc.RootElement.GetProperty("boundary").GetString());
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaRegistry_鍵與靜態目錄完全一致()
    {
        string json = InterchangeAndReportFactsService.GenerateSchemaRegistryJson();
        var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("staticKeys").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(FactKeyCatalog.Keys, keys);
    }

    // ── RS-001 匿名求助範本 ────────────────────────────────────────────────

    [Fact]
    public void 匿名求助範本_敏感值與序號遮蔽()
    {
        var facts = new List<HardwareFact>
        {
            Fact("cpu.name", "Test CPU"),
            Fact("board.serial", "SECRET-123"),
            Fact("fs.path", "C:\\Users\\ somebody"),
        };

        string text = InterchangeAndReportFactsService.BuildAnonymousHelpTemplate(facts);

        Assert.Contains("| cpu.name | cpu.name | Test CPU | Present", text);
        Assert.Contains("[已遮蔽]", text);
        Assert.DoesNotContain("SECRET-123", text);
        Assert.DoesNotContain("C:\\", text);
    }

    [Fact]
    public void 匿名求助範本_機器型號可自願提供_但仍為單行()
    {
        string text = InterchangeAndReportFactsService.BuildAnonymousHelpTemplate([], "Test Machine\r\nSecond Line");
        Assert.Contains("- 機器型號（使用者可自願提供）：Test Machine Second Line", text);
    }

    [Fact]
    public void 匿名求助範本_空值標無值_不假裝0()
    {
        string text = InterchangeAndReportFactsService.BuildAnonymousHelpTemplate([Fact("empty.key", "")]);
        Assert.Contains("| empty.key | empty.key | （無值） | Present", text);
    }

    // ── RS-003 SVG ─────────────────────────────────────────────────────────

    [Fact]
    public void SVG_標題與標籤會轉義_中文保留()
    {
        string svg = InterchangeAndReportFactsService.BuildSvgChart("溫度 <熱> & 壓力",
            [("CPU & <溫>", 72), ("GPU", 55)]);

        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("溫度 &lt;熱&gt; &amp; 壓力", svg);
        Assert.Contains("CPU &amp; &lt;溫&gt;", svg);
        Assert.Contains("CPU &amp; &lt;溫&gt;", svg);
        Assert.Contains(">72<", svg);
        Assert.Contains(">55<", svg);
    }

    [Fact]
    public void SVG_負數與非有限值不入圖()
    {
        string svg = InterchangeAndReportFactsService.BuildSvgChart("test",
            [("bad", -1), ("nan", double.NaN), ("ok", 1)]);
        Assert.DoesNotContain(">bad<", svg);
        Assert.DoesNotContain(">nan<", svg);
        Assert.Contains(">ok<", svg);
        Assert.Equal(1, svg.Split("<rect x=\"160\"", StringSplitOptions.RemoveEmptyEntries).Length - 1);
    }

    [Fact]
    public void SVG_空資料畫空圖不假裝有柱()
    {
        string svg = InterchangeAndReportFactsService.BuildSvgChart("empty", []);
        Assert.DoesNotContain("<rect x=\"160\"", svg);
    }

    // ── RS-004 xlsx ────────────────────────────────────────────────────────

    [Fact]
    public async Task Xlsx_可開啟成Zip且含工作表與固定表頭()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "facts.xlsx");
        var facts = new List<HardwareFact>
        {
            Fact("cpu.name", "Test CPU"),
            Fact("board.serial", "SECRET", sensitive: true),
        };
        try
        {
            await InterchangeAndReportFactsService.WriteXlsxAsync(path, facts);
            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet1.xml");
            string sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();

            Assert.Contains("<t xml:space=\"preserve\">key</t>", sheet);
            Assert.Contains("<t xml:space=\"preserve\">Test CPU</t>", sheet);
            Assert.Contains("<t xml:space=\"preserve\">（敏感值已遮蔽）</t>", sheet);
            Assert.DoesNotContain("SECRET", sheet, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Xlsx_Preserve政策明示才保留敏感值()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "facts.xlsx");
        var facts = new List<HardwareFact> { Fact("board.serial", "SECRET", sensitive: true) };
        try
        {
            await InterchangeAndReportFactsService.WriteXlsxAsync(path, facts, SensitiveValuePolicy.Preserve);
            using var zip = ZipFile.OpenRead(path);
            string sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
            Assert.Contains("SECRET", sheet);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Xlsx_目標目錄不存在會拒絕_不隱式建立()
    {
        string missing = Path.Combine(TempDir(), "not-exists", "facts.xlsx");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            InterchangeAndReportFactsService.WriteXlsxAsync(missing, []));
    }

    // ── RS-005 列印版面 ────────────────────────────────────────────────────

    [Fact]
    public void 列印版面_復用HTML服務_含列印樣式與敏感遮蔽()
    {
        var facts = new List<HardwareFact>
        {
            Fact("cpu.name", "Test CPU"),
            Fact("board.serial", "SECRET", sensitive: true),
        };
        string html = InterchangeAndReportFactsService.BuildPrintHtml(facts, "列印測試", At);

        Assert.Contains("@media print", html);
        Assert.Contains("Test CPU", html);
        Assert.DoesNotContain("SECRET", html);
        Assert.Contains("SHA256:", html);
    }

    // ── RS-012 離線手冊 ────────────────────────────────────────────────────

    [Fact]
    public void 離線手冊_由HelpCatalog產生_自足可列印()
    {
        string html = InterchangeAndReportFactsService.BuildOfflineManualHtml("測試手冊", At);

        Assert.Contains("<title>測試手冊</title>", html);
        Assert.Contains("@media print", html);
        Assert.Contains("HelpCatalog 的離線再呈現", html);
        Assert.Contains("說明條目 " + HelpCatalog.All.Count.ToString(CultureInfo.InvariantCulture) + " 條。", html);
    }

    [Fact]
    public void 離線手冊_不含外部資源()
    {
        string html = InterchangeAndReportFactsService.BuildOfflineManualHtml();
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────────

    [Fact]
    public void 本批十一把鍵齊()
    {
        // 10 → 11：第 541 項補上 XT-003（xt.rest，明文不做）。
        // 它與其餘十把不同類——其餘是「產生某種輸出」的描述，它是「刻意不提供」的聲明；
        // 但同樣是這批要對外講清楚的事，所以一起計數。
        var facts = InterchangeAndReportFactsService.Collect(At);
        string[] expected =
        [
            "xt.csharp", "xt.py", "xt.trigger", "xt.snapshot.fs", "xt.registry", "xt.rest",
            "rs.helpfmt", "rs.svg", "rs.xlsx", "rs.print", "rs.manual",
        ];
        foreach (string key in expected)
            Assert.Contains(facts, f => f.Key == key);
        Assert.Equal(11, facts.Count);
    }

    [Fact]
    public void 全部描述鍵標Derived且零網路()
    {
        var all = InterchangeAndReportFactsService.Collect(At).ToList();
        foreach (var f in all)
        {
            // xt.rest 是例外且<b>必須</b>是例外：它「明文不做」，值是空的、信任度是 Unknown。
            // 這裡刻意不把它排除在掃描外，而是明確斷言它的例外形狀——
            // 若哪天有人把它改成 Derived 並填了值，代表紅線被移動了，這條會紅。
            // 其餘所有描述鍵都應該誠實描述自己並聲明零網路。
            if (f.Key == "xt.rest")
            {
                Assert.Equal(FactTrustLevel.Unknown, f.Trust);
                Assert.Equal(FactAvailability.NotSupported, f.Availability);
                Assert.Equal("", f.Value);
                continue;
            }

            Assert.Equal(FactTrustLevel.Derived, f.Trust);
            Assert.Contains("零網路", f.Value);
        }
    }
}
