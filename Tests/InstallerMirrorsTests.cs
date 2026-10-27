using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 一鍵安裝器（<c>Installer/Install.cs</c>）的守門。
///
/// <para>
/// 這個 exe 是使用者第一個碰到的東西：它抓錯了檔案、或把分享頁存成
/// 「XinSpect.exe」，使用者會雙擊一個打不開的檔案，而且不知道是誰的錯。
/// 偏偏它跑在 .NET Framework 的 csc 上（只能 C# 5），沒辦法在測試專案裡
/// 直接 new 出來測——所以這裡驗的是「原始碼與成品之間對不對得上」：
/// 選單編號、各來源的模式、下載後驗 PE 的規則，以及最重要的
/// 「改了 Install.cs 有沒有重編 exe」。
/// </para>
/// </summary>
public sealed class InstallerMirrorsTests
{
    private static string Source => File.ReadAllText(Path.Combine(RepoRoot(), "Installer", "Install.cs"));

    private static string MirrorList =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Installer", "mirrors.txt"));

    [Fact]
    public void 選單編號固定是三格且第一格是GitHub()
    {
        string src = Source;
        int one = src.IndexOf("Key = \"1\"", StringComparison.Ordinal);
        int two = src.IndexOf("Key = \"2\"", StringComparison.Ordinal);
        int three = src.IndexOf("Key = \"3\"", StringComparison.Ordinal);

        Assert.True(one > 0, "找不到編號 1：使用者按 1 要能下載");
        Assert.True(two > one, "編號 2 必須在 1 之後（選單順序就是按鍵順序）");
        Assert.True(three > two, "編號 3 必須在 2 之後");

        string first = src.Substring(one, two - one);
        Assert.Contains("GitHub", first);
        Assert.Contains("Direct = true", first);
        // 1 號永遠指向 latest/download：不能寫死版本，否則舊安裝器會停在舊版。
        Assert.Contains("GitHubBase", first);
        Assert.Contains("releases/latest/download/", src);
        Assert.DoesNotContain("releases/download/v", src);
    }

    [Fact]
    public void 第二格是GoogleDrive且走檔案的直連端點()
    {
        string src = Source;
        int two = src.IndexOf("Key = \"2\"", StringComparison.Ordinal);
        int three = src.IndexOf("Key = \"3\"", StringComparison.Ordinal);
        string block = src.Substring(two, three - two);

        Assert.Contains("Google Drive", block);
        Assert.Contains("uc?export=download&id=", block);
    }

    [Fact]
    public void GoogleDrive回確認頁時會改走usercontent的confirm直連()
    {
        // Google 對執行檔常先回一張「無法掃描病毒」的網頁（HTTP 200），
        // 換成 drive.usercontent.google.com 並帶 confirm=t 才是檔案本體。
        string src = Source;
        Assert.Contains("drive.usercontent.google.com/download?id=", src);
        Assert.Contains("confirm=t", src);
        Assert.Contains("DriveConfirmUrl", src);
    }

    [Fact]
    public void 第三格是中國網盤且只能是分享頁模式()
    {
        string src = Source;
        int three = src.IndexOf("Key = \"3\"", StringComparison.Ordinal);
        int after = src.IndexOf("string text = FetchMirrorList()", StringComparison.Ordinal);
        Assert.True(after > three);

        string block = src.Substring(three, after - three);
        Assert.Contains("網盤", block);
        // 網盤沒有穩定直連：寫成 direct 就是把「下載到 HTML」當成功。
        Assert.Contains("Direct = false", block);

        // mirrors.txt 裡真的填了 3 的時候也必須是 page。
        var line = MirrorList.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("3|", StringComparison.Ordinal) && !l.StartsWith("#"));
        if (line is not null)
        {
            Assert.EndsWith("|page", line);
        }
    }

    [Fact]
    public void 鏡像清單每一行都合法而且沒有留死的預留字()
    {
        var lines = MirrorList.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            string[] parts = line.Split('|');
            Assert.Equal(3, parts.Length);
            Assert.Contains(parts[0], new[] { "1", "2", "3" });
            Assert.StartsWith("https://", parts[1]);
            Assert.Contains(parts[2], new[] { "direct", "page" });
            // 佔位符絕不能留在要發佈的檔案裡。
            Assert.DoesNotContain("<", parts[1]);
            Assert.DoesNotContain("xxxx", parts[1], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 下載回來的東西不是程式就要擋下來並刪掉()
    {
        string src = Source;
        Assert.Contains("LooksLikeProgram", src);
        Assert.Contains("MZ", src);
        // 只報錯不刪檔＝使用者資料夾裡留著一個假 exe，下次還是會雙擊它。
        Assert.Contains("File.Delete(dest)", src);
        Assert.Contains("不是 Windows 程式", src);
        // 也要把「內容像是什麼」寫出來，不然「失敗」兩個字沒辦法分辨是限流頁還是分享頁。
        Assert.Contains("FirstLine(", src);
    }

    [Fact]
    public void 已提交的安裝器是從這份原始碼編出來的()
    {
        // 這條抓的是「改了 Install.cs 忘了重編」——本機跑得好好的，上傳的還是舊 exe。
        // 用 UTF-16（.NET 中繼資料裡的字串編碼）找一句只存在於新版選單的話。
        string exe = Path.Combine(RepoRoot(), "Installer", "XinSpectDeploy.exe");
        Assert.True(File.Exists(exe), "Installer/XinSpectDeploy.exe 不見了");

        byte[] bytes = File.ReadAllBytes(exe);
        Assert.True(bytes.Length < 20 * 1024,
            $"安裝器 {bytes.Length} bytes：超過 20 KB 就是有人把執行階段塞進來了");

        Assert.True(Contains(bytes, Encoding.Unicode.GetBytes("選擇下載來源")),
            "exe 裡找不到新版選單的字串——Install.cs 改了但沒有重跑 Tools\\build-installer.ps1");
        Assert.True(Contains(bytes, Encoding.Unicode.GetBytes("123 雲盤")),
            "exe 裡沒有網盤那一格");
    }

    [Fact]
    public void 發佈流程會重編安裝器而不是直接上傳舊檔()
    {
        // release.ps1 上傳的是 Installer/XinSpectDeploy.exe；沒有先重編，上傳的就是上一次的成品。
        string release = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "release.ps1"));
        Assert.Contains("build-installer.ps1", release);
        Assert.Contains("Installer\\XinSpectDeploy.exe", release);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
