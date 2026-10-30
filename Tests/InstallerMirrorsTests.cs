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

    /// <summary>
    /// 去掉註解後的原始碼，給「不該出現某個詞」這類否定斷言用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼需要這一把：這個倉庫的寫作習慣是把踩過的坑寫進註解（「WebClient 的
    /// DownloadProgressChanged 同步下載不會回呼」，那段話正是叫人不要用它）。
    /// 對整份原始碼做 <c>DoesNotContain</c>，會讓「解釋為什麼不用」的註解變成違規——
    /// 守門因此懲罰它自己鼓勵的寫法。
    /// </para>
    /// <para>
    /// 否定斷言必須看**程式碼**：字串常值保留（`"DownloadProgressChanged"` 出現在
    /// 字串裡就是真的在用），`//`、`///` 與 `/* */` 則整段拿掉。這是一支小狀態機，
    /// 不是正規式——`https://…` 這種字串裡的雙斜線不能被當成行註解。
    /// </para>
    /// </remarks>
    private static string Code
    {
        get
        {
            string src = Source;
            var sb = new StringBuilder(src.Length);
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];

                // 字串（含逐字字串）與字元常值：原樣搬過去
                if (c == '"' || c == '\'')
                {
                    bool verbatim = c == '"' && i > 0 && src[i - 1] == '@';
                    char quote = c;
                    sb.Append(c);
                    i++;
                    while (i < src.Length)
                    {
                        char d = src[i];
                        sb.Append(d);
                        if (verbatim)
                        {
                            if (d == '"' && i + 1 < src.Length && src[i + 1] == '"')
                            {
                                sb.Append('"');
                                i += 2;
                                continue;
                            }
                        }
                        else if (d == '\\' && i + 1 < src.Length)
                        {
                            sb.Append(src[i + 1]);
                            i += 2;
                            continue;
                        }
                        i++;
                        if (d == quote) break;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }

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
    public void 下載時要顯示進度條速度與剩餘時間()
    {
        string src = Source;

        // 三樣都要有，而且是畫出來的字元，不是只有變數名。
        Assert.Contains("BarWidth", src);
        Assert.Contains("new string('#', fill)", src);
        Assert.Contains("MB/s", src);
        Assert.Contains("ETA ", src);

        // 速度必須是「位元組 ÷ 實測秒數」；拿百分比當速度是這一類進度列的經典錯誤。
        Assert.Contains("_speed = (received - _lastBytes) * 1000.0 / dMs", src);

        // 伺服器沒給 Content-Length（分塊傳輸）時不得湊百分比：
        // 那條分支只寫已下載量，剩餘時間寫 --，而不是從 0 慢慢爬到 100。
        Assert.Contains("expected <= 0", src);
        Assert.Contains("ETA --:--", src);

        // 剩餘時間向上取整：顯示 0:00 卻還在跑，比不顯示更糟。
        Assert.Contains("Math.Ceiling(seconds)", src);

        // 第一格取樣沒有前一個區間可分，用「從按下到現在」的平均，而不是沿用 0。
        Assert.Contains("_lastMs == 0", src);
    }

    [Fact]
    public void 進度列只能單行覆寫而且不會留下殘影()
    {
        string src = Source;
        // 用 \r 回到行首、用空白補到上一次的寬度：否則 42% 變 9% 時尾巴會留著舊字。
        Assert.Contains("Console.Write(\"\\r\" + text)", src);
        Assert.Contains("new string(' ', _printed - text.Length)", src);
        // Done 那一行要蓋掉最後一格進度列，不能印在下一行。
        Assert.Contains("public void Overwrite(string text)", src);
    }

    [Fact]
    public void 進度列不得掛回WebClient的同步下載()
    {
        // WebClient.DownloadProgressChanged 只在非同步那一條路觸發；同步的 DownloadFile
        // 一次也不會回呼。上一版就是掛在它上面，所以進度列從來沒有出現過——
        // 這條擋的是「覺得自己讀回包太囉唆，改回兩行程式碼」。
        //
        // 用 Code（去掉註解）而不是 Source：Install.cs 裡有一段說明正好寫到這兩個名字，
        // 那是「告訴你為什麼不要用」，不是「用了」。
        string code = Code;
        Assert.DoesNotContain("DownloadProgressChanged", code);
        Assert.DoesNotContain("wc.DownloadFile", code);
        Assert.DoesNotContain("DownloadFileAsync", code);
        Assert.Contains("while ((n = src.Read(buf, 0, buf.Length)) > 0)", code);
        // 實收不等於 Content-Length 就是斷線。截短檔有 MZ 頭、也夠大，會被 LooksLikeProgram 放行。
        Assert.Contains("received != expected", code);
    }

    [Fact]
    public void 進度列要跟主控台一樣寬而且重導向時不會爆()
    {
        string src = Source;
        // 80 欄主控台上整行 79 字；更寬的視窗它會自己長大，但永遠留一格。
        // 折行的代價是 \r 只回到最後一行的行首，每畫一次就多一行殘影。
        Assert.Contains("Console.WindowWidth", src);
        Assert.Contains("int cols = 80;", src);
        Assert.Contains("MinBarWidth", src);
        Assert.Contains("MaxBarWidth", src);
    }

    [Fact]
    public void 去註解工具自己不能弄壞斷言的依據()
    {
        // Code 是上面那條否定斷言的基礎，所以它自己也要被驗一次：
        //   ① 註解要真的消失（否則否定斷言形同虛設）；
        //   ② 字串裡雙斜線不能當行註解（`https://…` 一整條 URL 會跟著消失，
        //      那樣「releases/latest/download/ 還在不在」這種肯定斷言也會誤判）；
        //   ③ 程式碼本體要原樣留著。
        string code = Code;

        // ①：Install.cs 的說明段落裡有這些詞，程式碼裡沒有
        Assert.DoesNotContain("假成功", code);
        Assert.DoesNotContain("噪訊", code);

        // ②：URL 字串必須活著
        Assert.Contains("https://github.com/Xinglanclever/XinSpect/releases/latest/download/", code);
        Assert.Contains("https://raw.githubusercontent.com/Xinglanclever/XinSpect/main/Installer/mirrors.txt", code);

        // ③：真正的程式碼不能一起被削掉
        Assert.Contains("private sealed class ProgressBar", code);
        Assert.Contains("req.UserAgent = \"XinSpectDeploy\"", code);
        Assert.Contains("LooksLikeProgram", code);

        // 逐字字串（@@""）與跳脫字串都不能把狀態機帶歪
        Assert.Contains("\"\\r\"", code);
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
        // 進度列是這一版才有的字串：少了它代表上傳的還是舊 exe。
        Assert.True(Contains(bytes, Encoding.Unicode.GetBytes("MB/s")),
            "exe 裡沒有下載速度的字串——Install.cs 改了但沒有重跑 Tools\\build-installer.ps1");
    }

    [Fact]
    public void 發佈流程會重編安裝器而不是直接上傳舊檔()
    {
        // release.ps1 上傳的是 Installer/XinSpectDeploy.exe；沒有先重編，上傳的就是上一次的成品。
        string release = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "release.ps1"));
        Assert.Contains("build-installer.ps1", release);
        Assert.Contains("Installer\\XinSpectDeploy.exe", release);

        // 建置腳本自己也要跑進度列的驗證：光「重編了」不夠，重編出來的東西得畫得出進度列。
        string build = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "build-installer.ps1"));
        Assert.Contains("ProgressTest.cs", build);
        Assert.Contains("xinspect-progress-test.exe", build);
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
