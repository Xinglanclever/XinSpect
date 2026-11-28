using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Source-catalog coverage is deliberately separate from visual-tree tests.
/// Every app-owned XAML display/accessibility literal must have a real English
/// entry; native/user/hardware values are not rewritten to satisfy this guard.
/// </summary>
public sealed class EnglishCatalogCoverageTests
{
    private static readonly HashSet<string> UiProperties = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "ToolTip", "Title",
        "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    private static readonly Regex Han = new(@"[\u3400-\u9fff]", RegexOptions.CultureInvariant);
    private static readonly Regex FormatToken = new(
        @"(?<!\{)\{[0-9]+(?:,-?[0-9]+)?(?::[^{}]*)?\}(?!\})",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EveryVisibleXamlLiteralHasAnEnglishCatalogEntry()
    {
        var missing = new List<string>();
        var files = SourceXamlFiles().ToArray();
        Assert.NotEmpty(files);
        int inspected = 0;
        foreach (string path in files)
        {
            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            foreach (var element in document.Descendants())
            foreach (string text in DisplayTexts(element))
            {
                if (!Han.IsMatch(text)) continue;
                inspected++;
                string? english = EnglishStrings.Lookup(text);
                if (string.IsNullOrWhiteSpace(english) || Han.IsMatch(english))
                    missing.Add($"{Path.GetRelativePath(RepositoryRoot(), path)}: {text}");
            }
        }
        Assert.True(inspected > 0, "The source scan did not inspect any display text.");
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EnglishCatalogValuesContainNoUntranslatedHanText()
    {
        foreach (string key in EnglishStrings.Keys)
        {
            string? english = EnglishStrings.Lookup(key);
            Assert.False(string.IsNullOrWhiteSpace(english), $"Empty translation: {key}");
            Assert.False(Han.IsMatch(english!), $"Untranslated English entry: {key} => {english}");
        }
    }

    [Fact]
    public void CatalogTemplatesPreserveEveryArgumentAndNumericFormat()
    {
        foreach (string key in EnglishStrings.Keys)
        {
            var sourceTokens = FormatToken.Matches(key).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
            var englishTokens = FormatToken.Matches(EnglishStrings.Lookup(key)!)
                .Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
            Assert.True(sourceTokens.SequenceEqual(englishTokens), $"Changed format arguments: {key}");
        }
    }

    [Theory]
    [InlineData("CPU 封裝功耗被動診斷", "Passive CPU package power diagnostics")]
    [InlineData("目前控制器時脈與副時序", "Current controller clocks and secondary timings")]
    [InlineData("Raw Input 到達間隔", "Raw Input Arrival Intervals")]
    [InlineData("Driver Store 套件盤點與備份", "Driver Store Inventory and Backup")]
    [InlineData("磁碟空間分析", "Disk Space Analysis")]
    [InlineData("自訂感測器警示", "Custom Sensor Alerts")]
    [InlineData("自訂感測面板", "Custom Sensor Panel")]
    public void NewlyVisibleFeaturesHaveSpecificEnglishNames(string source, string expected)
        => Assert.Equal(expected, EnglishStrings.Lookup(source));

    [Fact]
    public void UnknownDataIsNotReplacedByGenericEnglishText()
    {
        // The catalog has exact source semantics. These represent device/user
        // payloads and cannot become “Unknown” merely because they contain Han.
        Assert.Null(EnglishStrings.Lookup("使用者自訂的裝置名稱 xyz-20261128"));
        Assert.Null(EnglishStrings.Lookup(@"C:\Users\使用者\硬體報告.json"));
        Assert.Null(EnglishStrings.Lookup(@"PCI\VEN_8086&DEV_A1B2 未收錄裝置"));
    }

    private static IEnumerable<string> SourceXamlFiles()
    {
        string root = RepositoryRoot();
        var files = Directory.EnumerateFiles(root, "*.xaml", SearchOption.TopDirectoryOnly);
        foreach (string folder in new[] { "Views", "Controls", "Dialogs", "Themes" })
            files = files.Concat(Directory.EnumerateFiles(Path.Combine(root, folder), "*.xaml", SearchOption.AllDirectories));
        return files.Where(path => !Path.GetFileName(path).Contains("LocalInference", StringComparison.Ordinal));
    }

    private static IEnumerable<string> DisplayTexts(XElement element)
    {
        foreach (var attribute in element.Attributes())
        {
            if (!UiProperties.Contains(attribute.Name.LocalName)) continue;
            string text = attribute.Value;
            if (text.StartsWith('{'))
            {
                const string marker = "StringFormat=";
                int start = text.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0) continue;
                text = text[(start + marker.Length)..];
                if (text.StartsWith("{}", StringComparison.Ordinal)) text = text[2..];
                text = text[..^1].Trim('\"', '\'');
            }
            yield return text;
        }
        if (element.Name.LocalName == "Setter" && UiProperties.Contains((string?)element.Attribute("Property") ?? ""))
        {
            string value = (string?)element.Attribute("Value") ?? "";
            if (!value.StartsWith('{')) yield return value;
        }
        // Includes text after a nested Run or LineBreak, without inventing one
        // joined key that could hide untranslated individual display nodes.
        foreach (var node in element.Nodes().OfType<XText>())
            yield return node.Value.Trim();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XinSpect.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
