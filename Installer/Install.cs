using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

internal static class Install
{
    // 一個下載來源。Direct=true 時 BaseUrl 是「檔案所在目錄」，安裝器自己抓；
    // Direct=false 時 BaseUrl 是分享頁，安裝器只開瀏覽器並說清楚要按什麼。
    // 網盤（尤其 123 雲盤）沒有穩定的直連，假裝能直連只會讓使用者卡在一個
    // 下載到 HTML 的「XinSpect.exe」上，所以兩者分開處理，不混在一起。
    private sealed class Mirror
    {
        public string Key;
        public string Name;
        public string BaseUrl;
        public bool Direct;
    }

    private const string GitHubBase = "https://github.com/Xinglanclever/XinSpect/releases/latest/download/";

    // 鏡像連結的「最新版」放在版控裡，安裝器開機時順手抓一次（3 秒逾時，拿不到就用內建的）。
    // 這樣上傳完只要改這一個文字檔，不必重編 exe——也是「連結還沒生出來就先別硬編死」的解。
    private const string MirrorListUrl = "https://raw.githubusercontent.com/Xinglanclever/XinSpect/main/Installer/mirrors.txt";

    // 內建預設：留空＝這一版還沒有這個鏡像（選單上如實寫「尚未提供」，不會偷偷退回 GitHub）。
    private const string GoogleDriveFileId = "";
    private const string ChinaPanShareUrl = "";

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Console.WriteLine();
        Console.WriteLine("  ==========================================");
        Console.WriteLine("     XiLan XinSpect Auto Deploy");
        Console.WriteLine("  ==========================================");
        Console.WriteLine();

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

        List<Mirror> all = LoadMirrors();
        Mirror chosen = ChooseMirror(all, args);
        if (chosen == null) { Pause(); return 2; }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string dir = Path.Combine(desktop, "XinSpect");

        Console.WriteLine("  Source : " + chosen.Name);
        Console.WriteLine("  Target : " + dir);
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(dir);

            if (!chosen.Direct)
            {
                // 分享頁：只開瀏覽器，不假裝自己抓得到。網盤的下載要過頁面上的按鈕，
                // 有的還要登入或驗證碼——安裝器不該代使用者按，也不該把分享頁存成 exe。
                Console.WriteLine("  [1/1] 已開啟分享頁。請在瀏覽器按「下載」，把 XinSpect.exe 放到：");
                Console.WriteLine("        " + dir);
                Console.WriteLine("        " + chosen.BaseUrl);
                Process.Start(new ProcessStartInfo(chosen.BaseUrl) { UseShellExecute = true });

                Console.WriteLine();
                Console.WriteLine("  ==========================================");
                Console.WriteLine("  下載完成後，把 XinSpect.exe 放進上面那個資料夾就能直接執行。");
                Console.WriteLine("  想全自動部署（含守護進程）請重新執行本程式並按 1（GitHub）。");
                Console.WriteLine("  ==========================================");
                Console.WriteLine();
                Pause();
                return 0;
            }

            Console.WriteLine("  [1/4] Downloading XinSpect.exe (includes LHM + WinRing0 driver + BlueSquadron)...");
            DownloadFile(chosen, "XinSpect.exe", Path.Combine(dir, "XinSpect.exe"), 1, 4);

            Console.WriteLine("  [2/4] Downloading BlueSquadronBridge.exe...");
            DownloadFile(chosen, "BlueSquadronBridge.exe", Path.Combine(dir, "BlueSquadronBridge.exe"), 2, 4);

            Console.WriteLine("  [3/4] Deployed to Desktop\\XinSpect folder.");

            Console.WriteLine("  [4/4] Launching XinSpect...");
            Console.WriteLine();
            Process.Start(new ProcessStartInfo(Path.Combine(dir, "XinSpect.exe")) { UseShellExecute = true });

            Console.WriteLine("  ==========================================");
            Console.WriteLine("  Deploy complete.");
            Console.WriteLine("  - XinSpect.exe (includes WinRing0 + LHM + BlueSquadron)");
            Console.WriteLine("  - BlueSquadronBridge.exe (standalone guard)");
            Console.WriteLine("  Location: Desktop\\XinSpect\\");
            Console.WriteLine("  ==========================================");
            Console.WriteLine();
            Console.WriteLine("  Press any key to close...");
            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("  Deploy failed: " + ex.Message);
            Console.WriteLine();
            Console.WriteLine("  Manual download:");
            Console.WriteLine("    https://github.com/Xinglanclever/XinSpect/releases");
            Console.WriteLine();
            Console.WriteLine("  Press any key to close...");
            Pause();
            return 1;
        }
    }

    private static List<Mirror> LoadMirrors()
    {
        List<Mirror> list = new List<Mirror>();
        list.Add(new Mirror
        {
            Key = "1",
            Name = "GitHub Releases（官方，永遠是最新版）",
            BaseUrl = GitHubBase,
            Direct = true
        });
        list.Add(new Mirror
        {
            Key = "2",
            Name = "Google Drive（鏡像）",
            BaseUrl = GoogleDriveFileId.Length == 0 ? "" : "https://drive.google.com/uc?export=download&id=" + GoogleDriveFileId,
            Direct = true
        });
        list.Add(new Mirror
        {
            Key = "3",
            Name = "中國網盤（123 雲盤分享頁）",
            BaseUrl = ChinaPanShareUrl,
            Direct = false
        });

        string text = FetchMirrorList();
        if (text != null) ApplyMirrorList(list, text);
        return list;
    }

    // 抓 mirrors.txt；任何問題（離線、逾時、格式怪）都回 null，用內建值。
    // 這一支不能讓安裝器卡住——鏡像本來就是「GitHub 連不上時」才用的。
    private static string FetchMirrorList()
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(MirrorListUrl);
            req.UserAgent = "XinSpectDeploy";
            req.Timeout = 3000;
            req.ReadWriteTimeout = 3000;
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }
        catch
        {
            return null;
        }
    }

    // 每行：key|url|direct 或 key|url|page。只認 http/https；看不懂的行直接跳過。
    private static void ApplyMirrorList(List<Mirror> list, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] parts = line.Split('|');
            if (parts.Length < 2) continue;

            string key = parts[0].Trim();
            string url = parts[1].Trim();
            if (!url.StartsWith("https://") && !url.StartsWith("http://")) continue;

            foreach (Mirror m in list)
            {
                if (m.Key != key) continue;
                m.BaseUrl = url;
                if (parts.Length >= 3) m.Direct = parts[2].Trim().ToLowerInvariant() == "direct";
            }
        }
    }

    private static string Url(Mirror m, string file)
    {
        return m.BaseUrl.EndsWith("/") ? m.BaseUrl + file : m.BaseUrl + "/" + file;
    }

    // 選來源：按鍵或命令列參數（參數是為了讓這一頁能被機器驗，不必真的有人坐在前面按）。
    private static Mirror ChooseMirror(List<Mirror> all, string[] args)
    {
        if (args != null && args.Length > 0)
        {
            string want = args[0].Trim();
            foreach (Mirror m in all)
            {
                if (m.Key == want && m.BaseUrl.Length > 0) return m;
            }
            Console.WriteLine("  無法使用來源「" + want + "」（沒有提供或沒有這個編號）。");
            Console.WriteLine();
            return null;
        }

        Console.WriteLine("  選擇下載來源：");
        Console.WriteLine();
        foreach (Mirror m in all)
        {
            Console.WriteLine("    [" + m.Key + "] " + m.Name + (m.BaseUrl.Length == 0 ? "  —— 尚未提供" : ""));
        }
        Console.WriteLine();
        Console.Write("  按 1 / 2 / 3 選擇（直接按 Enter 用 1，GitHub）：");

        string key = "";
        try
        {
            ConsoleKeyInfo k = Console.ReadKey(true);
            key = k.KeyChar.ToString();
            Console.WriteLine(key.Length == 0 ? "" : " " + key);
        }
        catch
        {
            Console.WriteLine();
        }
        Console.WriteLine();

        if (key.Length == 0) return all[0];
        foreach (Mirror m in all)
        {
            if (m.Key == key && m.BaseUrl.Length > 0) return m;
        }

        Console.WriteLine("  來源「" + key + "」這版還沒有提供（見上面的「尚未提供」），改用 GitHub。");
        Console.WriteLine();
        return all[0];
    }

    private static void Pause()
    {
        try { Console.ReadKey(true); } catch { }
    }

    private static void DownloadFile(Mirror m, string name, string dest, int step, int total)
    {
        string url = m.Direct ? Url(m, name) : m.BaseUrl;
        Stopwatch sw = Stopwatch.StartNew();
        using (var wc = new WebClient())
        {
            wc.Headers.Add("User-Agent", "XinSpectDeploy");
            wc.DownloadProgressChanged += (s, e) =>
            {
                Console.Write(string.Format("\r  [{2}/{3}] {0}%  ({1} / {4} MB)   ",
                    e.ProgressPercentage,
                    e.BytesReceived / 1048576,
                    step, total,
                    e.TotalBytesToReceive / 1048576));
            };
            wc.DownloadFile(new Uri(url), dest);
        }
        sw.Stop();

        // 下載到 HTML 是這種安裝器最常見的假成功：分享頁、流量限制頁、病毒掃描警告
        // 都會回 200，存下來的「XinSpect.exe」其實是一張網頁。這裡直接驗 PE 標頭，
        // 不合格就刪掉並說清楚，不讓使用者雙擊一個不是程式的檔案。
        if (!LooksLikeProgram(dest))
        {
            // Google Drive 對執行檔常常先回一張「無法掃描病毒」的確認頁，
            // 同一個檔案換成 usercontent 端點並帶 confirm=t 就會直給檔案。
            string retry = DriveConfirmUrl(url);
            if (retry != null)
            {
                File.Delete(dest);
                Console.WriteLine();
                Console.WriteLine("  Google Drive 回了確認頁，改用確認直連重試…");
                using (var wc2 = new WebClient())
                {
                    wc2.Headers.Add("User-Agent", "XinSpectDeploy");
                    wc2.DownloadFile(new Uri(retry), dest);
                }
            }
            if (!LooksLikeProgram(dest))
            {
                long len = new FileInfo(dest).Length;
                string line = FirstLine(Head(dest, 400));
                try { File.Delete(dest); } catch { }
                throw new InvalidDataException(string.Format(
                    "{0} 下載回來的不是 Windows 程式（{1:N0} bytes，開頭不是 MZ；內容像是：{2}）。請改用其他來源。",
                    name, len, line));
            }
        }

        FileInfo fi = new FileInfo(dest);
        Console.WriteLine(string.Format("\r  [{2}/{3}] Done: {0:N0} bytes ({1:F1}s)   ",
            fi.Length, sw.Elapsed.TotalSeconds, step, total));
    }

    private static string DriveConfirmUrl(string url)
    {
        if (url.IndexOf("drive.google.com", StringComparison.OrdinalIgnoreCase) < 0) return null;
        int at = url.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string rest = url.Substring(at + 3);
        int amp = rest.IndexOf('&');
        string id = amp >= 0 ? rest.Substring(0, amp) : rest;
        if (id.Length == 0) return null;
        return "https://drive.usercontent.google.com/download?id=" + id + "&export=download&confirm=t";
    }

    private static bool LooksLikeProgram(string path)
    {
        try
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Length < 1024 * 100) return false;
            return Head(path, 2).StartsWith("MZ", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Head(string path, int count)
    {
        byte[] buf = new byte[count];
        int n;
        using (FileStream fs = File.OpenRead(path))
        {
            n = fs.Read(buf, 0, count);
        }
        char[] chars = new char[n];
        for (int i = 0; i < n; i++) chars[i] = buf[i] < 0x20 || buf[i] > 0x7E ? ' ' : (char)buf[i];
        return new string(chars);
    }

    private static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(new char[] { '\r', '\n' });
        string line = cut > 0 ? text.Substring(0, cut) : text;
        line = line.Trim();
        if (line.Length > 80) line = line.Substring(0, 80) + "...";
        return line.Length == 0 ? "（無法讀出內容）" : line;
    }
}
