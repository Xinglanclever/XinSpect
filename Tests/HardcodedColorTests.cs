using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 底色與文字色不得寫死色碼——除了說得出理由的少數例外。
///
/// 這一份存在的理由：淺色主題壞掉的方式不是拋例外，而是「某一塊維持深色，而文字已經變成近黑」，
/// 於是那塊區域整片看不見。說明點的問號提示、超頻頁的提示、瀏覽器的網址列都真的這樣壞過。
/// 例外一律要在 <see cref="Allowed"/> 裡寫下原因，不能默默加一個色碼進去。
/// </summary>
public class HardcodedColorTests
{
    /// <summary>整份檔案豁免（連同原因）：這些畫面的顏色是內容本身，不是佈景。</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["Theme.xaml"] = "佈景色盤本身的初值，執行期由 ThemeService 換掉",
        ["ScreenTestWindow.xaml"] = "螢幕壞點檢測：純黑／純白／純色是量測用的訊號，不能跟著主題變",
        ["MouseTestWindow.xaml"] = "滑鼠檢測：軌跡與按鍵示意圖的固定配色即是內容",
        ["KeyboardTestWindow.xaml"] = "鍵盤檢測：按鍵狀態（未按／按下／卡鍵）以固定色表示",
        ["SpeakerTestWindow.xaml"] = "喇叭檢測：聲道與掃頻的固定配色即是內容",
        ["MotionTestWindow.xaml"] = "動態檢測：拖影測試需要固定對比的黑白方塊",
        ["IconGalleryWindow.xaml"] = "徽章一覽：展示的就是各廠牌原色",
        ["BrandBadge.xaml"] = "主機板／處理器廠牌徽章：品牌色不隨主題改",
        ["TerminalView.xaml"] = "終端機沿用主控台的黑底淺字，與系統的 cmd.exe 一致",
    };

    /// <summary>逐行豁免（子字串比對）：警示／危險等語意色，與主題無關。</summary>
    private static readonly string[] AllowedLines =
    [
        "#B3202020",   // MainWindow 警示橫幅底：半透明暗底 ＋ 橘框，任何主題下都要醒目
        "#E0552E",     // 警示橫幅與一鍵裝機警告的橘
        "#FFE3D8",     // 警示橫幅文字（配上面那個橘底）
        "#33E0552E",
        "#333F8F5B",   // 一鍵裝機：已安裝的綠底標記
        "#3F8F5B",
        "#B3462B",     // 主機板「危險區」框線
        "#E06C4B",     // 主機板「危險區」標題與說明
        "#1b1b1b",     // 超頻頁「測試版」徽章：落在強調色底上的固定深字
    ];

    /// <summary>
    /// 兩種寫法都要抓：屬性直接寫（<c>Background="#..."</c>）與
    /// Setter 寫（<c>Property="Background" ... Value="#..."</c>，樣板與觸發程序都是這一種）。
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        new(@"(Background|Foreground)\s*=\s*""#[0-9a-fA-F]{6,8}"""),
        new(@"Property\s*=\s*""(?:[\w.]*\.)?(Background|Foreground)""[^>]*?Value\s*=\s*""#[0-9a-fA-F]{6,8}"""),
    ];

    [Fact]
    public void 底色與文字色不得寫死色碼()
    {
        var offenders = new List<string>();

        foreach (string path in Directory.EnumerateFiles(RepoRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string file = Path.GetFileName(path);
            if (Allowed.ContainsKey(file)) continue;

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Patterns.Any(p => p.IsMatch(lines[i]))) continue;
                if (AllowedLines.Any(a => lines[i].Contains(a))) continue;
                offenders.Add($"{file}:{i + 1}　{lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "底色／文字色寫死色碼，換主題時不會跟著變（淺色主題下常常變成看不見的一整塊）。"
            + "請改用佈景資源，或在 HardcodedColorTests 的豁免清單裡寫下理由：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// 自我驗證：上面那條若寫錯（例如只抓得到屬性寫法、抓不到 Setter 寫法），
    /// 整份檢查會靜默失效，比沒有檢查更糟。這裡拿兩種寫法的樣本各驗一次。
    /// </summary>
    [Fact]
    public void 兩種寫法都抓得到()
    {
        Assert.Contains(Patterns, p => p.IsMatch(@"<Border Background=""#26282C"">"));
        Assert.Contains(Patterns, p => p.IsMatch(@"<Setter Property=""Background"" Value=""#26282C""/>"));
        Assert.Contains(Patterns, p => p.IsMatch(@"<Setter TargetName=""bd"" Property=""Background"" Value=""#33FFFFFF""/>"));
        Assert.Contains(Patterns, p => p.IsMatch(@"<Setter Property=""TextElement.Foreground"" Value=""#ffffff""/>"));
        // 反面：佈景資源與非底色屬性都不該被誤判
        Assert.DoesNotContain(Patterns, p => p.IsMatch(@"<Border Background=""{DynamicResource SurfaceBrush}"">"));
        Assert.DoesNotContain(Patterns, p => p.IsMatch(@"<GradientStop Offset=""0"" Color=""#1b2740""/>"));
        Assert.DoesNotContain(Patterns, p => p.IsMatch(@"<local:Oscilloscope TraceBrush=""#35E06A""/>"));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "XinSpect.csproj"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
