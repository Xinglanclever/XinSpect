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
    // 要用哪一種，看的是**端點本身**：只有真的能用 GET 取回目錄下檔案的
    // 線路（GitHub Release、自建 CDN、碼雲附件那一類）才寫 direct。網盤的分享頁不是
    // 目錄、直連又會過期，假裝能直連只會讓使用者卡在一個下載到 HTML 的「XinSpect.exe」上。
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

    // 中國直連線路：目錄型端點（能用 GET 取回該目錄下的檔名），不是網盤分享頁。
    // 留空＝還沒有；填了就按 3 由安裝器自己抓，跟按 1 一樣有進度條與速度。
    private const string ChinaDirectBase = "";

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
            Name = "中國直連線路（鏡像）",
            BaseUrl = ChinaDirectBase,
            Direct = true
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
        ProgressBar bar = RunDownload(url, dest, step, total);

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
                bar = RunDownload(retry, dest, step, total);
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

        sw.Stop();
        FileInfo fi = new FileInfo(dest);
        double secs = sw.Elapsed.TotalSeconds;
        // 整段的平均速度：和進度列上的即時速度是兩件事，這一條是「從按下到現在」的實測。
        double avg = secs > 0 ? fi.Length / 1048576.0 / secs : 0;
        bar.Overwrite(string.Format(
            "  [{0}/{1}] Done: {2:N0} bytes in {3:F1}s ({4:0.0} MB/s)",
            step, total, fi.Length, secs, avg));
    }

    // 一次下載＝一條進度列。回傳它，是為了讓最後那行 Done 蓋掉進度列，而不是印在下一行。
    //
    // 為什麼自己讀回包而不是用 WebClient：WebClient 的 DownloadProgressChanged 只在非同步
    // （DownloadFileAsync）那一條路會觸發，同步的 DownloadFile 一次也不會回呼——上一版就是
    // 掛在這個上面，所以進度列根本沒出現過。自己讀還多了一件事能做：比對 Content-Length 與
    // 實收位元組數。連線中途斷掉時 Stream.Read 只會回 0，不丟例外，截短過的 exe 照樣有 MZ
    // 開頭、照樣過 LooksLikeProgram——那是比「下載失敗」更難查的假成功。
    private static ProgressBar RunDownload(string url, string dest, int step, int total)
    {
        ProgressBar bar = new ProgressBar(step, total);
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "XinSpectDeploy";
            req.AllowAutoRedirect = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            // 不開 AutomaticDecompression：開了之後實收位元組會比 Content-Length 多，
            // 進度就會跑過 100%，到時要解釋的變成進度條而不是下載。
            using (WebResponse resp = req.GetResponse())
            {
                long expected = resp.ContentLength;
                long received = 0;
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = File.Create(dest))
                {
                    byte[] buf = new byte[64 * 1024];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        received += n;
                        bar.Report(received, expected);
                    }
                    dst.Flush();
                }
                if (expected > 0 && received != expected)
                {
                    throw new InvalidDataException(string.Format(
                        "連線中途斷開：只收到 {0:N0} / {1:N0} bytes。請改用其他來源或重試。",
                        received, expected));
                }
            }
        }
        catch
        {
            // 半截的檔案比沒有檔案更危險：它有 MZ 開頭、有大小的確像個程式。
            TryDelete(dest);
            bar.ClearLine();
            throw;
        }
        return bar;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// 下載進度列：進度條、即時速度、剩餘時間。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 安裝器最怕的不是慢，是「看起來在下載」。所以這裡每一格都有出處：
    /// 百分比只在伺服器給了 Content-Length 時才算；速度是最近一個取樣區間內的
    /// 實測位元組除以實測時間；剩餘時間是剩餘位元組除以那個速度。
    /// </para>
    /// <para>
    /// 量不到的一律寫「--」，不印一個看起來很肯定的假數字——分塊傳輸沒有
    /// Content-Length，硬湊一個百分比只會讓「卡在 100%」變成另一種誤導。
    /// </para>
    /// </remarks>
    private sealed class ProgressBar
    {
        // 進度條的長度不是固定的：預設主控台寬 80 欄，而這一行除了條子本身還有
        // 「[1/4] 」「100.0%」「123.4/1234.5 MB」「12.3 MB/s」「ETA 1:23」。
        // 條子固定 24 格時整行是 83 字——超過 80 就自動折行，而 \r 只回到「最後一行」的
        // 行首，於是每畫一次就多留一行殘影，十秒後整個畫面都是半條進度列。
        // 所以條子寬度依實際視窗算，寧可短一點，也不要折行。
        private const int MaxBarWidth = 24;
        private const int MinBarWidth = 8;
        // 扣除條子以外的固定欄位：head 8 + "[] " 3 + 百分比 6 + 大小 17 + 速度 14 + ETA 11
        private const int FixedColumns = 59;

        private readonly int _step;
        private readonly int _total;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        private long _lastBytes;
        private long _lastMs;
        private long _nextMs;
        private long _nextPaintMs;
        private double _speed;
        private int _printed;

        public ProgressBar(int step, int total)
        {
            _step = step;
            _total = total;
        }

        /// <summary>目前可用的條子寬度。輸出被重導向時 WindowWidth 會丟例外，那就退回 80。</summary>
        private static int BarWidth
        {
            get
            {
                int cols = 80;
                try
                {
                    int w = Console.WindowWidth;
                    if (w >= 40) cols = w;
                }
                catch { }
                int width = cols - FixedColumns;
                if (width < MinBarWidth) width = MinBarWidth;
                if (width > MaxBarWidth) width = MaxBarWidth;
                return width;
            }
        }

        public void Report(long received, long expected)
        {
            long ms = _sw.ElapsedMilliseconds;
            if (_lastMs == 0)
            {
                // 第一坨只記基準點，不算速度：從 0 ms 到 1 ms 之間除出來的數字不是速度，是噪訊。
                // 寧可先顯示「--」，也不要讓使用者看到一個馬上就會被推翻的誇張值。
                _lastMs = ms;
                _lastBytes = received;
                _nextMs = ms + 400;
            }
            else if (ms >= _nextMs)
            {
                // 速度每 400 ms 取樣一次：太密速度會跳，太疏進度列會一頓一頓。
                long dMs = ms - _lastMs;
                if (dMs > 0) _speed = (received - _lastBytes) * 1000.0 / dMs;
                _lastBytes = received;
                _lastMs = ms;
                _nextMs = ms + 400;
            }
            // 重畫每 100 ms 一次就夠：每讀到一坨就重畫，50 MB/s 時一秒寫 800 次主控台。
            if (ms >= _nextPaintMs || _nextPaintMs == 0)
            {
                Write(Line(received, expected));
                _nextPaintMs = ms + 100;
            }
        }

        public void Overwrite(string text)
        {
            Write(text);
            Console.WriteLine();
            _printed = 0;
        }

        /// <summary>下載失敗時把進度列擦掉，讓下一行的錯誤訊息從乾淨的行首開始。</summary>
        public void ClearLine()
        {
            if (_printed <= 0) return;
            Console.Write("\r" + new string(' ', _printed) + "\r");
            _printed = 0;
        }

        private void Write(string text)
        {
            // 這一行全是 ASCII，所以字數就是欄位數，補空白不會錯位。
            if (text.Length < _printed) text += new string(' ', _printed - text.Length);
            _printed = text.Length;
            Console.Write("\r" + text);
        }

        private string Line(long received, long expected)
        {
            string head = string.Format("  [{0}/{1}] ", _step, _total);
            string speed = _speed > 0
                ? string.Format("{0,6:0.0} MB/s", _speed / 1048576.0)
                : "    -- MB/s";

            if (expected <= 0)
            {
                // 伺服器沒給 Content-Length：沒有分母，就不編一個百分比。
                return head + string.Format("{0,8:0.0} MB", received / 1048576.0)
                     + "  " + speed + "  ETA --:--";
            }

            double pct = received * 100.0 / expected;
            if (pct > 100) pct = 100;
            int barWidth = BarWidth;
            int fill = (int)(pct * barWidth / 100.0);
            if (fill < 0) fill = 0;
            if (fill > barWidth) fill = barWidth;

            double eta = -1;
            if (_speed > 0 && received < expected) eta = (expected - received) / _speed;

            return head
                 + "[" + new string('#', fill) + new string('-', barWidth - fill) + "] "
                 + string.Format("{0,5:0.0}%", pct)
                 + string.Format(" {0,6:0.0}/{1,6:0.0} MB", received / 1048576.0, expected / 1048576.0)
                 + "  " + speed
                 + "  ETA " + Eta(eta);
        }

        private static string Eta(double seconds)
        {
            if (seconds < 0 || seconds > 359999) return "--:--";
            long s = (long)Math.Ceiling(seconds);
            if (s < 3600) return string.Format("{0}:{1:00}", s / 60, s % 60);
            return string.Format("{0}:{1:00}:{2:00}", s / 3600, (s % 3600) / 60, s % 60);
        }
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
