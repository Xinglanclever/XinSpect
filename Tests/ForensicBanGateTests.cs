using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 隱私禁項機制化（Vol 2 批次 B／FS-013）：Jump lists／MRU（RecentDocs、RunMRU、TypedPaths、TypedURLs）、
/// UserAssist、縮圖快取（ThumbCache）——這些來源的<b>明文內容</b>是隱私禁項，本工具明文不讀。
/// 禁令機器釘死：掃描器對假內容咬人（正對照），正式碼全庫零命中（負對照）。
/// </summary>
public class ForensicBanGateTests
{
    /// <summary>禁項來源樣式。加新禁項要在這裡登記並在註解寫明為何禁。</summary>
    public static readonly string[] BannedPatterns =
    [
        // Jump lists（AutomaticDestinations／CustomDestinations）：記錄使用者開過什麼檔、什麼順序——行為側寫。
        "JumpList",
        "AutomaticDestinations",
        "CustomDestinations",
        // MRU 族（登錄 UserAssist／Explorer MRU）：開檔與程式啟動的使用習慣，與鑑識目標無關的隱私。
        "RecentDocs",
        "RunMRU",
        "TypedPaths",
        "TypedURLs",
        "UserAssist",
        // 縮圖快取（thumbcache_*.db／ThumbCache）：影像內容殘留，純隱私。（比對不分大小寫，登一個樣式即可）
        "ThumbCache",
    ];

    /// <summary>掃描 (檔名, 內容) 清單，回命中（檔名, 樣式）。禁令只掃<b>正式碼</b>；測試檔與文件不受限。</summary>
    public static IReadOnlyList<(string File, string Pattern)> Scan(IEnumerable<(string Name, string Content)> files)
    {
        var hits = new List<(string, string)>();
        foreach (var (name, content) in files)
        {
            foreach (string pattern in BannedPatterns)
            {
                if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    hits.Add((name, pattern));
            }
        }
        return hits;
    }

    private static IEnumerable<(string Name, string Content)> ProductionSources()
    {
        string root = RepoRoot();
        foreach (string dir in new[] { "Services", "XinSpect.Decoders", "ViewModels", "Views", "Models", "Nav" })
        {
            string path = System.IO.Path.Combine(root, dir);
            if (!System.IO.Directory.Exists(path)) continue;
            foreach (string file in System.IO.Directory.EnumerateFiles(path, "*.cs", System.IO.SearchOption.AllDirectories)
                         .Concat(System.IO.Directory.EnumerateFiles(path, "*.xaml", System.IO.SearchOption.AllDirectories)))
            {
                string name = System.IO.Path.GetFileName(file);
                // FS-013 的宣告檔（禁項清單本身）是字典不是讀取行為——它列出不讀的東西，不該被自己咬
                if (name.Equals("EnvironmentRuntimeFactsService.cs", StringComparison.Ordinal)) continue;
                yield return (name, System.IO.File.ReadAllText(file));
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        return dir?.FullName ?? throw new System.IO.DirectoryNotFoundException();
    }

    [Fact]
    public void 掃描器咬人_含禁項字樣的檔案要被抓到()
    {
        var hits = Scan(
        [
            ("clean.cs", "// 這裡只講 AppCompatCache 與 MountedDevices"),
            ("bad.cs", "var jumplists = ReadJumpLists();"),
            ("bad2.cs", "// THUMBCACHE 路徑"),
        ]);
        Assert.Equal(2, hits.Count);
        Assert.Equal(("bad.cs", "JumpList"), hits[0]);
        Assert.Equal(("bad2.cs", "ThumbCache"), hits[1]);
    }

    [Fact]
    public void 正式碼零命中_禁項來源明文不讀()
    {
        var files = ProductionSources().ToList();
        Assert.True(files.Count > 50, "掃到的正式碼檔案數異常少——掃描路徑壞了等於禁令失效");
        var hits = Scan(files);
        Assert.True(hits.Count == 0,
            "正式碼出現隱私禁項來源：" + string.Join("；", hits.Select(h => h.File + "→" + h.Pattern))
            + "。FS-013 禁令：Jump lists／MRU／UserAssist／縮圖的明文內容本工具不讀——"
            + "若你認為該放行，先把理由寫進本測試的禁項表註解並取得使用者同意，而不是直接刪樣式。");
    }

    [Fact]
    public void 禁項表不能縮水()
    {
        Assert.True(BannedPatterns.Length >= 9, "禁項樣式被刪——禁令清單只許增不許減");
    }
}
