using System;
using System.IO;
using System.Xml.Linq;
using XinSpectCli;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// xsc（XinSpect 終端機）的守門：獨立專案要留在版控、主專案的 glob 不得把它吃回去。
/// </summary>
/// <remarks>
/// 2026-11-04 的實際事故：XinSpectCli/ 是主專案目錄的子目錄，主 csproj 的預設
/// `**/*.cs` glob 把 Program.cs 編進主程式，兩個 Main 直接 CS0017。
/// 修法與 Bridge/ 相同——主 csproj 明確 Remove；這裡守住那個 Remove 不被拿掉。
/// </remarks>
public class XinSpectCliTests
{
    [Fact]
    public void 補全清單必須覆蓋分派switch的所有case()
    {
        // 「兩處寫死會漂移」的同款病（RelayLimitParityTests 同款守門）：
        // Program.cs 的 Tab 補全清單從 CommandBook 推導，這裡釘住——
        // Execute() 裡出現的每一個 case "X"，都必須能在補全來源（CommandBook 條目名＋別名）裡找到。
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpectCli", "Program.cs"));
        var cases = new System.Text.RegularExpressions.Regex("case .?([a-z?]+)")
            .Matches(src).Select(m => m.Groups[1].Value).ToHashSet();

        string book = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpectCli", "CommandBook.cs"));
        var names = new System.Text.RegularExpressions.Regex("new.?.?([a-z?]+)")
            .Matches(book).Select(m => m.Groups[1].Value).ToHashSet();

        // BuildCommands 補的別名（文檔外可用指令）
        names.UnionWith(new[] { "refresh", "mascot", "吉祥物", "百科", "quit", "q", "?", "shortcuts" });
        Assert.Contains("手冊.md 是 v2.91 時代的歷史文件", src, StringComparison.Ordinal);
        Assert.DoesNotContain("case \"catalog\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog <關鍵字>", book, StringComparison.Ordinal);

        var missing = cases.Where(c => !names.Contains(c)).ToList();
        Assert.True(missing.Count == 0,
            "分派 switch 有、補全清單沒有：" + string.Join("、", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 主專案必須排除XinSpectCli的原始碼()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpect.csproj"));
        Assert.Contains("Compile Remove=\"XinSpectCli\\**\\*.cs\"", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI專案進入點與離線百科可用()
    {
        string root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "XinSpectCli.csproj")), "缺 XinSpectCli.csproj");
        string src = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Program.cs"));
        // Claude Code 式體驗的最低門檻：互動迴圈＋歷史＋補全＋唯讀承諾
        Assert.Contains("ReadLineWithHistory", src, StringComparison.Ordinal);
        Assert.Contains("ConsoleKey.Tab", src, StringComparison.Ordinal);
        Assert.Contains("ConsoleKey.UpArrow", src, StringComparison.Ordinal);
        Assert.Contains("唯讀", src, StringComparison.Ordinal);
        // Claude Code 式介面（2026-11-05 裁決）：圓角歡迎框、星芒 spinner、> 提示符
        string tui = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Tui.cs"));
        // 吉祥物 ☼（U+263C 白日光芒＝「曦」）取代 Claude 的 ✻；破曉序列 spinner；> 提示符
        Assert.Contains("╭", tui, StringComparison.Ordinal);
        Assert.Contains("☼", tui, StringComparison.Ordinal);
                // 〔修復 v3.21〕P4-23：歡迎行改由 FactKeyCatalog 驅動（不再硬編 1,178——鍵數變動不再漂）
        Assert.Contains("走訪 {FactKeyCatalog.Count:N0} 把靜態事實鍵中", tui, StringComparison.Ordinal);
        Assert.Contains("∙", tui, StringComparison.Ordinal);   // 破曉序列起始幀
        // ✻ 僅允許出現在寬度註解（VisibleLength 的字元區間註記），介面字串一律用 ☼
        // ✻ 不得出現在歡迎框本體（VisibleLength 的字元區間註解除外）
        int wStart = tui.IndexOf("public static void Welcome", StringComparison.Ordinal);
        int wEnd = tui.IndexOf("// ── 2)", StringComparison.Ordinal);
        Assert.True(wStart >= 0 && wEnd > wStart, "找不到 Welcome 方法區段");
        Assert.DoesNotContain("✻", tui[wStart..wEnd], StringComparison.Ordinal);
        Assert.DoesNotContain("xs▸", tui, StringComparison.Ordinal);
        // 指令書與百科：help 分類功能表＋單指令詳解；book 離線嵌入整本百科
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "CommandBook.cs")), "缺 CommandBook.cs");
        Assert.Contains("class CommandBook", File.ReadAllText(Path.Combine(root, "XinSpectCli", "CommandBook.cs")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "Book.cs")), "缺 Book.cs");
        string csproj = File.ReadAllText(Path.Combine(root, "XinSpectCli", "XinSpectCli.csproj"));
        Assert.Contains("docs" + Path.DirectorySeparatorChar + "百科-v3.17.md", csproj, StringComparison.Ordinal);
        // 不得引入寫入通路
        Assert.DoesNotContain("WritePciConfig", src, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMsrPair", src, StringComparison.Ordinal);

        string markdown = File.ReadAllText(Path.Combine(root, "docs", "百科-v3.17.md"));
        var headings = System.Text.RegularExpressions.Regex.Matches(markdown,
            @"^## (第\s*(\d+)\s*部[^\r\n]*)", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Contains(headings, h => h.Groups[2].Value == "0");
        var originalOut = Console.Out;
        try
        {
            foreach (System.Text.RegularExpressions.Match heading in headings)
            {
                using var output = new StringWriter();
                Console.SetOut(output);
                Book.Run(heading.Groups[2].Value);
                Assert.Contains(heading.Groups[1].Value, output.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("沒有第", output.ToString(), StringComparison.Ordinal);
            }
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void CLI輸入區只畫提交後的單條長分隔線()
    {
        string root = RepoRoot();
        string tui = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Tui.cs"));
        int start = tui.IndexOf("public static void InputFrame()", StringComparison.Ordinal);
        int end = tui.IndexOf("public static int CurrentWidth()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 CLI 輸入框方法");
        Assert.DoesNotContain("new string('─'", tui[start..end], StringComparison.Ordinal);

        string program = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Program.cs"));
        Assert.Contains("Tui.InputFrame();", program, StringComparison.Ordinal);
        int readLine = program.IndexOf("private static string? ReadLineWithHistory()", StringComparison.Ordinal);
        int submit = program.IndexOf("if (key.Key == ConsoleKey.Enter)", readLine, StringComparison.Ordinal);
        int separator = program.IndexOf("new string('─', Math.Max(1, w - 1))", submit, StringComparison.Ordinal);
        Assert.True(readLine >= 0 && submit > readLine && separator > submit,
            "Enter 後應輸出單條、按視窗寬度計算的分隔線");
        Assert.Contains("Console.WriteLine();", program[submit..separator], StringComparison.Ordinal);
    }

    [Fact]
    public void CLI語言選項支援繁簡並移除全域參數()
    {
        Assert.True(CliLocalization.TryConsumeOption(
            ["--lang", "zh-CN", "facts"], out var remaining, out var error));
        Assert.Null(error);
        Assert.Equal(["facts"], remaining);
        Assert.Equal(CliLanguage.Simplified, CliLocalization.Current);
        Assert.Equal("内存与缓存", CliLocalization.Translate("記憶體與快取"));
        Assert.Equal("未知参数", CliLocalization.Translate("未知引數"));

        Assert.True(CliLocalization.TrySet("zh-TW"));
        Assert.Equal("記憶體與快取", CliLocalization.Translate("記憶體與快取"));
    }

    [Fact]
    public void CLI語言選項不會轉換JSON或SARIF輸出()
    {
        Assert.True(CliLocalization.IsMachineFormat(["facts", "--json"]));
        Assert.True(CliLocalization.IsMachineFormat(["compliance", "--format", "sarif"]));
        Assert.True(CliLocalization.IsMachineFormat(["--errors"]));
        Assert.True(CliLocalization.IsMachineFormat(["--stdin-batch"]));
        Assert.True(CliLocalization.IsMachineFormat(["--compliance", "--format", "junit"]));
        Assert.True(CliLocalization.IsMachineFormat(["--enterprise-baseline", "policy.json"]));
        Assert.True(CliLocalization.IsMachineFormat([CliService.AgentBridgeArg, "capabilities"]));
        Assert.False(CliLocalization.IsMachineFormat(["facts", "--lang", "zh-CN"]));
        Assert.False(CliLocalization.IsMachineFormat(["--help"]));

        Assert.False(CliLocalization.TryConsumeOption(["--lang"], out _, out var missingError));
        Assert.Contains("requires", missingError, StringComparison.OrdinalIgnoreCase);
        Assert.False(CliLocalization.TryConsumeOption(["--lang", "fr-FR"], out _, out var unsupportedError));
        Assert.Contains("Unsupported", unsupportedError, StringComparison.Ordinal);

        Assert.True(CliLocalization.TryConsumeOption(["--lang", "zh-T", "help"], out var translated, out var langError), langError);
        Assert.Equal(["help"], translated);
        Assert.True(CliLocalization.TrySet("en"));
        Assert.Equal(CliLanguage.English, CliLocalization.Current);
        Assert.Contains("K", CliLocalization.LanguageCycleLabel, StringComparison.Ordinal);
        CliLocalization.ToggleEnglishSimplified();
        Assert.Equal(CliLanguage.Simplified, CliLocalization.Current);
        CliLocalization.ToggleEnglishSimplified();
        Assert.Equal(CliLanguage.English, CliLocalization.Current);
        Assert.Contains("K", CliLocalization.LanguageCycleLabel, StringComparison.Ordinal);
        CliLocalization.TrySet("zh-T");
        Assert.Equal(CliLanguage.Traditional, CliLocalization.Current);
    }

    [Fact]
    public void CLI文字輸出包裝器會跟隨互動中的語言切換()
    {
        var buffer = new StringWriter();
        using var writer = CliLocalization.WrapWriter(buffer);
        try
        {
            Assert.True(CliLocalization.TrySet("zh-TW"));
            writer.WriteLine("記憶體與快取");
            writer.Write('髮');
            writer.WriteLine();
            Assert.True(CliLocalization.TrySet("zh-CN"));
            writer.WriteLine("記憶體與快取");
            writer.Write('髮');
            writer.WriteLine();

            Assert.Equal("記憶體與快取" + Environment.NewLine + "髮" + Environment.NewLine
                + "内存与缓存" + Environment.NewLine + "发" + Environment.NewLine,
                buffer.ToString());
        }
        finally { CliLocalization.TrySet("zh-TW"); }
    }

    [Fact]
    public void LiveTop在窄終端逐行顯示全部欄位並保留GPU與記憶體數據()
    {
        string[][] labelsByLanguage =
        [
            ["CPU 溫度", "CPU 占用", "CPU 功率", "CPU 頻率", "GPU 溫度", "GPU 占用", "顯存 GB", "記憶體占用", "記憶體 GB"],
            ["CPU 温度", "CPU 占用", "CPU 功率", "CPU 频率", "GPU 温度", "GPU 占用", "显存 GB", "内存占用", "内存 GB"],
            ["CPU °C", "CPU %", "CPU W", "CPU GHz", "GPU °C", "GPU %", "VRAM GB", "MEM %", "MEM GB"],
        ];
        string[] languages = ["zh-T", "zh-S", "en"];
        string[] values = ["49.5 °C", "22.1%", "——", "4.20 GHz", "76.0 °C", "93.2%", "123456.0/654321.0 GB", "68.4%", "12.5/32.0 GB"];

        try
        {
            for (int languageIndex = 0; languageIndex < languages.Length; languageIndex++)
            {
                Assert.True(CliLocalization.TrySet(languages[languageIndex]));
                string[] labels = labelsByLanguage[languageIndex];
                int[] widths = labels.Select((label, index) =>
                    Math.Max(index is 6 or 8 ? 22 : 16, Tui.VisibleLength(label))).ToArray();
                string[] header = Tui.FormatLiveMetricCells("時間", labels, widths, 36, header: true);
                string[] data = Tui.FormatLiveMetricCells("12:34:56", values, widths, 36, header: false);
                string combined = string.Concat(header.Concat(data)).Replace(" ", string.Empty, StringComparison.Ordinal);

                Assert.All(header.Concat(data), line => Assert.InRange(Tui.VisibleLength(line), 0, 36));
                Assert.Contains(languageIndex == 2 ? "MEM%" : languageIndex == 1 ? "GPU占用" : "GPU占用", combined, StringComparison.Ordinal);
                Assert.Contains("93.2%", combined, StringComparison.Ordinal);
                Assert.Contains("123456.0/654321.0GB", combined, StringComparison.Ordinal);
                Assert.Contains("12.5/32.0GB", combined, StringComparison.Ordinal);
                Assert.True(header.Length > 1, "Narrow terminals must put remaining metric headings on continuation lines.");
                Assert.True(data.Length > 1, "Narrow terminals must put remaining metric values on continuation lines.");
            }
        }
        finally { CliLocalization.TrySet("zh-T"); }
    }

    [Theory]
    [InlineData("CPU Package", 52f, 52d)]
    [InlineData("Package", 1.75f, 1.75d)]
    [InlineData("CPU Package", 0f, null)]
    [InlineData("CPU Package", null, null)]
    [InlineData("CPU Package", float.NaN, null)]
    [InlineData("CPU Package", 2001f, null)]
    [InlineData("CPU Cores", 52f, null)]
    [InlineData("CPU Memory", 52f, null)]
    [InlineData("CPU Package (SMU rail)", 52f, null)]
    public void LivePowerUsesOnlyPlausiblePackageReadings(string label, float? raw, double? expected) =>
        Assert.Equal(expected, Tui.ReadPackageWatts(label, LibreHardwareMonitor.Hardware.SensorType.Power, raw));

    [Theory]
    [InlineData(RaplGateVerdict.Unknown, 75d, 52d, 52d, null, "unverified")]
    [InlineData(RaplGateVerdict.Trusted, 75d, 52d, 75d, 75d, "RAPL")]
    [InlineData(RaplGateVerdict.Trusted, null, 52d, 52d, null, "unverified")]
    [InlineData(RaplGateVerdict.NotResponding, 75d, 52d, null, null, "no usable reading")]
    [InlineData(RaplGateVerdict.NoCounter, 75d, null, null, null, "no usable reading")]
    public void LivePowerSeparatesSensorReadingsFromVerifiedPower(RaplGateVerdict verdict,
        double? direct, double? reported, double? display, double? verified, string source)
    {
        var reading = Tui.SelectCpuPower(direct, reported, verdict);
        Assert.Equal(display, reading.Watts);
        Assert.Equal(verified, reading.VerifiedWatts);
        try
        {
            Assert.True(CliLocalization.TrySet("en"));
            Assert.Contains(source, reading.SourceText, StringComparison.Ordinal);
            if (source == "unverified")
            {
                Assert.True(CliLocalization.TrySet("zh-T"));
                Assert.Contains("未驗證", reading.SourceText, StringComparison.Ordinal);
                Assert.True(CliLocalization.TrySet("zh-S"));
                Assert.Contains("未验证", reading.SourceText, StringComparison.Ordinal);
            }
        }
        finally { CliLocalization.TrySet("zh-T"); }
    }

    [Fact]
    public void MachineWriterBypassesNestedLocalizationWithoutChangingJson()
    {
        using var buffer = new StringWriter();
        var nested = CliLocalization.WrapWriter(CliLocalization.WrapWriter(buffer));
        const string json = "{\"sourceError\":\"記憶體與快取\"}";
        try
        {
            Assert.True(CliLocalization.TrySet("zh-CN"));
            Assert.Same(buffer, CliLocalization.MachineWriter(nested));
            CliLocalization.MachineWriter(nested).Write(json);
            Assert.Equal(json, buffer.ToString());
        }
        finally { CliLocalization.TrySet("zh-TW"); }
    }

    [Fact]
    public void InteractiveSensorJsonBypassesConsoleSynchronizationWrapper()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var buffer = new StringWriter();
        using var errors = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            Console.SetError(errors);
            Assert.True(CliLocalization.TrySet("zh-CN"));
            CliLocalization.InstallConsoleWriters(false);
            CliLocalization.InstallConsoleWriters(false);
            int code = CliService.Run([CliService.SensorCrossCheckArg],
                () => throw new InvalidOperationException("Full collection must not run."),
                CliLocalization.MachineWriter(Console.Out), Console.Error,
                sensorCapture: () => new([], [], "記憶體與快取"));

            Assert.Equal(CliService.ExitPartial, code);
            using var json = System.Text.Json.JsonDocument.Parse(buffer.ToString());
            Assert.Equal("記憶體與快取", json.RootElement.GetProperty("sourceError").GetString());
            Assert.Empty(errors.ToString());
            buffer.GetStringBuilder().Clear();
            Console.Write("記憶體與快取");
            Assert.Equal("内存与缓存", buffer.ToString());
            Assert.True(CliLocalization.IsMachineFormat([CliService.SensorCrossCheckArg]));
            Assert.True(CliLocalization.IsMachineFormat(["sensor-crosscheck"]));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            CliLocalization.TrySet("zh-TW");
        }
    }

    [Fact]
    public void CLI設定檢視會遮蔽金鑰密碼與token()
    {
        string json = """{"AiApiKey":"secret-key","nested":{"refreshToken":"secret-token","label":"visible"}}""";
        string safe = CliLocalization.RedactSettingsJson(json);

        Assert.DoesNotContain("secret-key", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", safe, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", safe, StringComparison.Ordinal);
        Assert.Contains("visible", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI深測引數解析保留含空格的暫存路徑()
    {
        string[] expected = ["deepbench", "quick", "server-workstation", "--storage", @"D:\Benchmark Drive\Run 1", "--confirm"];
        Assert.Equal(expected,
            CliLocalization.SplitCommandLine(CliLocalization.JoinCommandLine(expected)));
        Assert.Throws<FormatException>(() => CliLocalization.SplitCommandLine("quick --storage \"D:\\unfinished"));
    }

    [Fact]
    public void CLI要覆蓋常規版的功能面_GUI全頁對照()
    {
        // 使用者裁決：CLI 版要有常規（GUI）版的所有功能。載體是 pages／page／domain／
        // keys／settings／changelog——GUI 的 65 頁底下都是無頭服務，終端以事實查詢呈現同一批資料。
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpectCli", "Program.cs"));
        foreach (string fn in new[] { "ListPages", "PageFacts", "DomainFacts", "ListKeys", "ShowSettings", "ShowChangelog" })
            Assert.Contains(fn, src, StringComparison.Ordinal);
        // GUI 頁面清單就是對照表來源
        Assert.Contains("PageRegistry.Pages", src, StringComparison.Ordinal);
        Assert.Contains("PageRegistry.Utilities", src, StringComparison.Ordinal);
    }

    [Fact]
    public void CLILanguageKeyAndGlobeMascotDefaultsMatchTheRequestedInteraction()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpectCli", "Program.cs"));
        Assert.Contains("key.Key == ConsoleKey.K && buf.Length == 0", source, StringComparison.Ordinal);
        Assert.Contains("case \"shortcuts\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Shift+K", CliLocalization.LanguageCycleLabel, StringComparison.Ordinal);
        Assert.Equal(2, Tui.MascotChoice);
        Assert.Same(Tui.MascotCandidates[2], Tui.MascotFrames);
    }
}
