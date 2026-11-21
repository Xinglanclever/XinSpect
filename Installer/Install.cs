using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Principal;
using System.Threading;

/// <summary>
/// 曦覽部署器（XinSpectDeploy.exe）——畫風整版翻新（仿曦覽終端機 XinSpectCli 的瑞士平面語彙）。
/// </summary>
/// <remarks>
/// <para>
/// <b>畫風裁決：</b>與 xsc 同一個 Tui 語彙——品牌紅 #E30613、直角框（╭─╮，框線一律 CHA
/// 絕對定位，不做寬度計算）、■ 大寫小標、⏺／✕／⚠ 結果行、braille spinner、
/// ANSI Shadow 大字標。歡迎框／提示符／進度條全部對齊終端機的樣子。
/// </para>
/// <para>
/// <b>降級規則：</b>啟動時先打開 conhost 的 ENABLE_VIRTUAL_TERMINAL_PROCESSING；
/// 打不開（Windows 8.1、重導輸出、極舊主控台）就整體退回純文字——無色彩、無框線、
/// ASCII spinner、ASCII 進度條。所有「長得像圖案」的字元一律經 <see cref="Ui.Mark"/>，
/// 純文字版拿到的必是 ASCII。
/// </para>
/// <para>
/// <b>語法紀律：</b>這支仍由 .NET Framework 4.8 的 csc.exe 直編（見 Tools\build-installer.ps1），
/// 只能寫 C# 5——沒有插值字串、沒有 ?.／??=、沒有 expression-bodied member。編譯會紅，不會悄悄過。
/// </para>
/// <para>
/// <b>不變的部分：</b>下載與信任根邏輯一字未動——SHA256SUMS.txt 為唯一放行條件、
/// mirrors.txt v2 格式、下載備援鏈、PE／大小／斷線檢查。Installer\tests\ProgressTest.cs
/// 以反射直接驗編譯後 exe 的 ProgressBar 與 RunDownload，改壞了會被它擋下來。
/// </para>
/// </remarks>
internal static class Install
{
    // 部署器自己的版本號（UserAgent 與開場面板共用）。跟著主專案版號走，發版時順手改這裡。
    private const string Version = "3.28";

    // 一個下載來源。Direct=true 時 BaseUrl 是「檔案所在目錄」，安裝器自己抓；
    // Direct=false 時 BaseUrl 是分享頁，安裝器只開瀏覽器並說清楚要按什麼。
    // 要用哪一種，看的是**端點本身**：只有真的能用 GET 取回目錄下檔案的
    // 線路（GitHub Release、加速節點、自建 CDN）才寫 direct。網盤的分享頁不是
    // 目錄、直連又會過期，假裝能直連只會讓使用者卡在一個下載到 HTML 的「XinSpect.exe」上。
    private sealed class Mirror
    {
        public string Key;
        public string Name;
        public string BaseUrl;
        public bool Direct;
        // v2 direct 條目自報的 SHA-256（小寫 hex）。它**不是**放行條件——放行一律以官方
        // SHA256SUMS.txt 為準；這裡的值只用來偵測「鏡像清單與官方清單互相矛盾」的投毒。
        public string Sha256;

        public Mirror(string key, string name, string baseUrl, bool direct)
        {
            Key = key; Name = name; BaseUrl = baseUrl; Direct = direct;
        }

        public string Url(string file)
        {
            if (string.IsNullOrEmpty(BaseUrl)) return "";
            return BaseUrl.EndsWith("/") ? BaseUrl + file : BaseUrl + "/" + file;
        }
    }

    private const string GitHubBase = "https://github.com/Xinglanclever/XinSpect/releases/latest/download/";

    // 信任根（2）：官方總雜湊清單，與 key 1 同源、編譯期寫死，遠端清單不得改寫。
    // 發佈流程（Tools/sync-mirrors.py ＋ release.ps1）須把 SHA256SUMS.txt 與資產一起上傳
    // （sha256sum 慣例格式：<64位小寫hex>␣␣<裸檔名>）。
    private const string ShaSumsUrl = GitHubBase + "SHA256SUMS.txt";

    // 清單本體的大小上限：64KB 對幾十行鏡像綽綽有餘——超限的一律當「不是清單」丟棄，
    // 不給「清單抓取」這條路任何被餵大檔的意義。
    private const int MirrorListMaxBytes = 64 * 1024;

    // 鏡像連結的「最新版」放在版控裡，安裝器開機時順手抓一次（拿不到就用內建的）。
    // 這樣上傳完只要改這一個文字檔，不必重編 exe——也是「連結還沒生出來就先別硬編死」的解。
    // 抓取鏈：jsDelivr CDN → ghfast 加速 → raw.githubusercontent，任一條成功就夠。
    private static readonly string[] MirrorListUrls =
    {
        "https://cdn.jsdelivr.net/gh/Xinglanclever/XinSpect@main/Installer/mirrors.txt",
        "https://ghfast.top/https://raw.githubusercontent.com/Xinglanclever/XinSpect/main/Installer/mirrors.txt",
        "https://raw.githubusercontent.com/Xinglanclever/XinSpect/main/Installer/mirrors.txt",
    };

    // 內建預設。Drive 的分享資料夾不是目錄端點，內建值走 page 模式（開瀏覽器請使用者按「下載」）；
    // mirrors.txt 裡若列了真正的 direct 端點會在 ApplyMirrorList 覆寫。
    private const string GoogleDriveFolderUrl = "https://drive.google.com/drive/folders/1V3PoEMkee-iI6Ep2dk5r61vsxpohhYDs";

    // 中國加速節點：對 GitHub 加前綴的公開加速服務，安裝器自己抓。
    // 注意：走第三方節點的位元組只能驗 PE 標頭（見 DownloadFile），驗不了發佈者簽章——
    // 這是「連不上 GitHub」與「要不要被卡死」之間的取捨，所以它只當備援線路。
    private const string ChinaProxyBase = "https://ghfast.top/https://github.com/Xinglanclever/XinSpect/releases/latest/download/";
    private static readonly string[] GhProxies =
    {
        "https://ghfast.top/",
        "https://ghproxy.net/",
        "https://mirror.ghproxy.com/",
    };

    private static string _latestTag;

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        // 畫面件最先就位：VT 開不起來的環境整體退回純文字（見 Ui.EnableAnsi）。
        Ui.EnableAnsi();
        Ui.Welcome();

        // TLS13 在部分 Windows 上不存在：先試最全的，不行就退回穩態組合。
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 |
                                                   SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
        }
        catch
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; }
            catch { }
        }
        ServicePointManager.DefaultConnectionLimit = 32;

        // 第一層選單：裝什麼。A ＝ 主程式（GUI），B ＝ 曦覽終端機。
        // 第二層選單選下載來源（1/2/3）；命令列 -xsc 與來源編號供腳本／機器驗證用。
        bool xsc = ChooseProduct(args);
        bool legacy = false, nort = false, full = false;
        if (xsc) ChooseCliFlavor(args, out legacy, out nort);
        else full = ChooseDesktopFull(args);

        Ui.Busy("拉取鏡像清單 mirrors.txt");
        List<Mirror> all = LoadMirrors();
        Ui.BusyAbort();
        FlushNotices();

        Mirror chosen = ChooseMirror(all, args);
        if (chosen == null) { Pause(); return 2; }

        // 官方總雜湊清單：整個部署抓一次、所有檔共用。null＝一份都拿不到——
        // 各 Deploy* 內的 RequireHash 會據此拒裝並三態告知，不在這裡提早退出
        // （page 模式的「開瀏覽器」不需要雜湊，退出會把那條活路也關掉）。
        Ui.Busy("取得官方總雜湊清單 SHA256SUMS.txt");
        HashList hashes = FetchShaSums(chosen);
        if (hashes == null)
            Ui.BusyEnd(false, "無法取得官方總雜湊清單（SHA256SUMS.txt）——直連下載將被拒絕");
        else
            Ui.BusyEnd(true, "信任根就位：" + hashes.Via);

        return xsc ? DeployXsc(chosen, legacy, nort, hashes) : DeployGui(chosen, full, hashes);
    }

    // 遠端清單解析過程中的警告（對內建來源的覆寫、缺雜湊的 direct 條目）先收著——
    // spinner 還在轉的時候印出來會被 \r 重畫洗掉，等 BusyAbort 之後一次倒出來。
    private static readonly List<string> PendingNotices = new List<string>();

    private static void FlushNotices()
    {
        for (int i = 0; i < PendingNotices.Count; i++) Ui.Warn(PendingNotices[i]);
        PendingNotices.Clear();
    }

    // 決定某檔的「必過雜湊」。規則（分層加固的核心）：
    //  - 官方格（key 1）：SHA256SUMS 必須取得、必須含該檔、且取得來源必須是官方直連——
    //    GitHub 直連能下載就一定直連得了 SHA256SUMS，拿不到非官方來源的清單還硬裝，
    //    等於把「官方」兩個字交給加速節點定義。任一不過 → 例外 → 三態告知。
    //  - 其他 direct 來源（內建加速 key 3／遠端 v2 條目）：以官方總雜湊表為唯一放行條件；
    //    鏡像 v2 行自報的雜湊與官方表**矛盾**時，視為清單投毒，整個來源拒用。
    private static string RequireHash(HashList hl, Mirror m, string fileName)
    {
        if (hl == null)
            throw new InvalidOperationException(string.Format(
                "無法取得官方總雜湊清單（SHA256SUMS.txt）。{0}已強制雜湊驗證，無法驗證就不安裝。三個選項：\n" +
                "  ① 稍後重試（可能是 GitHub 暫時連不上）；\n" +
                "  ② 手動下載後自行核對：https://github.com/Xinglanclever/XinSpect/releases\n" +
                "  ③ 改用分享頁來源（瀏覽器下載，由你自行把關）。",
                m.Key == "1" ? "官方格" : "此來源"));
        if (m.Key == "1" && !hl.OfficialDirect)
            throw new InvalidOperationException(
                "官方來源的雜湊清單不是從官方直連取得的——拒絕在這種狀態下安裝「官方」版。請改用手動下載或稍後重試。");
        string official;
        if (!hl.ByName.TryGetValue(fileName, out official))
            throw new InvalidOperationException(string.Format(
                "總雜湊清單（來源：{0}）沒有「{1}」——此版未發佈該檔或清單過舊，拒絕安裝未知檔案。", hl.Via, fileName));
        if (m.Sha256 != null && !m.Sha256.Equals(official, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.Format(
                "鏡像清單聲明的雜湊與官方總表矛盾（鏡像 {0} vs 官方 {1}）——清單有被動過手腳的跡象，拒用此鏡像。",
                m.Sha256, official));
        return official;
    }

    // 第一層：按 A 裝主程式、按 B 裝曦覽終端機。命令列參數以 -xsc（或 --xsc）指定終端機路。
    private static bool ChooseProduct(string[] args)
    {
        if (args != null && Array.Exists(args, a =>
                a.Trim().Equals("-xsc", StringComparison.OrdinalIgnoreCase) ||
                a.Trim().Equals("--xsc", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        Ui.Prompt("按 A 或 B 選擇（直接按 Enter 預設 A，主程式）：");
        string key = ReadKeyOrEmpty();
        if (key.Length == 0) return false;   // Enter ＝ 主程式
        key = key.Trim().ToUpperInvariant();
        if (key == "B") return true;
        if (key == "A") return false;

        Ui.Warn("看不懂「" + key + "」，改裝主程式。");
        return false;
    }

    // 第二層（A：主程式）——帶不帶執行階段。
    // [1] 不帶環境：現在版 XinSpect.exe（約 36MB），機器要已裝 .NET 10 Desktop Runtime。
    // [2] 帶環境：XinSpect-full.exe，.NET 10 Desktop Runtime 一併打包，免裝但單檔大一截。
    // 命令列 -full 指定帶環境。
    private static bool ChooseDesktopFull(string[] args)
    {
        if (args != null && Array.Exists(args, a =>
                a.Trim().Equals("-full", StringComparison.OrdinalIgnoreCase) ||
                a.Trim().Equals("--full", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        Ui.Section("選版本", "1 不帶環境（約 36MB，需 .NET 10 Desktop Runtime）・2 帶環境（免裝，單檔較大）");
        Ui.Prompt("按 1 或 2（直接按 Enter 用 1）：");

        string key = ReadKeyOrEmpty();
        if (key.Length == 0) return false;   // Enter ＝ 不帶環境（現在版）
        if (key == "2") return true;
        if (key == "1") return false;

        Ui.Warn("看不懂「" + key + "」，用不帶環境的現在版。");
        return false;
    }

    // 第二層（B：終端機）——四版：兩個 Windows 世代 × 帶不帶執行階段。
    // 帶環境（self-contained）免裝 .NET；不帶環境（framework-dependent）約 60MB，要 .NET 10 Desktop Runtime。
    // Windows 10／Server 2016、2019、2022 用 legacy 版（純文字相容界面）。
    // 命令列 -legacy／-nort 分別指定相容版與不帶環境。
    private static void ChooseCliFlavor(string[] args, out bool legacy, out bool nort)
    {
        legacy = false;
        nort = false;
        if (args != null)
        {
            foreach (string a in args)
            {
                string t = (a ?? "").Trim();
                if (t.Equals("-legacy", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("--legacy", StringComparison.OrdinalIgnoreCase)) legacy = true;
                if (t.Equals("-nort", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("--nort", StringComparison.OrdinalIgnoreCase)) nort = true;
            }
            if (legacy || nort) return;   // 參數給齊就不問
        }

        Ui.Section("選版本", null);
        Ui.Item("1", "Windows 11／Server 2025・帶環境（免裝 .NET，約 100MB）");
        Ui.Item("2", "Windows 10／2016/2019/2022・帶環境（免裝 .NET，純文字界面，約 100MB）");
        Ui.Item("3", "Windows 11／Server 2025・不帶環境（約 60MB，需 .NET 10 Desktop Runtime）");
        Ui.Item("4", "Windows 10／2016/2019/2022・不帶環境（約 60MB，純文字界面，需 .NET 10 Desktop Runtime）");
        Ui.Prompt("按 1 / 2 / 3 / 4（直接按 Enter 用 1）：");

        string key = ReadKeyOrEmpty();
        if (key.Length == 0) return;                       // Enter ＝ 1
        if (key == "2") { legacy = true; return; }
        if (key == "3") { nort = true; return; }
        if (key == "4") { legacy = true; nort = true; return; }
        if (key == "1") return;

        Ui.Warn("看不懂「" + key + "」，用 Windows 11 帶環境版。");
    }

    /// <summary>讀一個鍵；重導輸入（機器驗證）時退回空字串——選單拿預設值。</summary>
    private static string ReadKeyOrEmpty()
    {
        try
        {
            string k = Console.ReadKey(intercept: true).KeyChar.ToString();
            Console.WriteLine(string.IsNullOrEmpty(k) ? "" : " " + k);
            return k;
        }
        catch { Console.WriteLine(); return ""; }
    }

    // xsc 部署：單檔下載 → 放進 Desktop\XinSpect\ → 詢問要不要開。
    // 終端機是自含單檔（含 .NET 10 執行階段），不需要 Bridge／驅動，一步到位。
    private static int DeployXsc(Mirror chosen, bool legacy, bool nort, HashList hashes)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = Path.Combine(desktop, "XinSpect");
            string dest = Path.Combine(dir, "XinSpectConsole.exe");
            // 四顆資產：兩個 Windows 世代 × 帶不帶執行階段。落地檔名一律 XinSpectConsole.exe——
            // 主程式強控台橋接找的是這個名字。
            string remoteName = legacy
                ? (nort ? "XinSpectConsole-legacy-nort.exe" : "XinSpectConsole-legacy.exe")
                : (nort ? "XinSpectConsole-nort.exe" : "XinSpectConsole.exe");
            string flavor = legacy ? "純文字相容版（Windows 10／Server 2016、2019、2022）"
                                   : "完整 UI 版（Windows 11／Server 2025）";
            flavor += nort ? "・不帶環境約 60MB，需 .NET 10 Desktop Runtime"
                           : "・自含執行階段約 100MB";

            Ui.Section("來源 / 目標", null);
            Ui.Info("來源", chosen.Name);
            Ui.Info("目標", dest);
            Ui.Rule();
            Directory.CreateDirectory(dir);

            if (!chosen.Direct)
            {
                // 分享頁：只開瀏覽器，不假裝自己抓得到。檔名用全名——xsc.exe 已不存在於 Release。
                Panel(chosen.BaseUrl);
                Console.WriteLine();
                Ui.Panel(Width(),
                    Ui.BoldMark() + " 手動放置",
                    new[]
                    {
                        "請在瀏覽器按「下載」，把 " + remoteName + " 放到：",
                        Ui.Dim + dir + Ui.Reset,
                        "下載完成後改名成 XinSpectConsole.exe，放進上面那個資料夾就能直接執行。",
                    },
                    null);
                Footer();
                Pause();
                return 0;
            }

            Ui.Section("1/2 · 下載", remoteName + "（" + flavor + "）");
            DownloadFileWithFallback(chosen, remoteName, dest, 1, 2, requireProgram: true, expectSha: RequireHash(hashes, chosen, remoteName));

            Console.WriteLine();
            Ui.Panel(Width(),
                Ui.BoldMark() + " 部署完成",
                new[]
                {
                    Ui.CheckMark() + " XinSpectConsole.exe" + Ui.Dim + "（曦覽終端機・唯讀硬體事實查詢）" + Ui.Reset,
                    Ui.Dim + "位置：" + dir + Ui.Reset,
                },
                null);

            Ui.Prompt("現在就開啟曦覽終端機嗎？按 Y 開啟，其他鍵跳過：");
            bool launch = false;
            try
            {
                ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                launch = k.Key == ConsoleKey.Y;
                Console.WriteLine(k.KeyChar.ToString());
            }
            catch { Console.WriteLine(); }
            if (launch && File.Exists(dest))
            {
                // 直接啟動終端機本身；不借 cmd /k——那會留下一個 cmd 外殼。
                // nort 版機器沒裝 .NET 10 Desktop Runtime 會起不來：如實說，不讓成功的部署被當成失敗。
                try
                {
                    Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
                    Ui.Ok("已啟動 XinSpectConsole.exe");
                }
                catch (Exception launchEx)
                {
                    Ui.Warn("啟動失敗（" + launchEx.Message + "）。如果是 nort（不帶環境）版本，請先安裝 .NET 10 Desktop Runtime：");
                    Ui.Info("", "https://dotnet.microsoft.com/download/dotnet/10.0");
                }
            }
            Footer();
            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            return DeployFailed(ex);
        }
    }

    private static int DeployGui(Mirror chosen, bool full, HashList hashes)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = Path.Combine(desktop, "XinSpect");

            Ui.Section("來源 / 目標", null);
            Ui.Info("來源", chosen.Name);
            Ui.Info("目標", dir);
            Ui.Rule();
            Directory.CreateDirectory(dir);

            if (!chosen.Direct)
            {
                // 分享頁：只開瀏覽器。網盤的下載要過頁面上的按鈕，有的還要登入或驗證碼——
                // 安裝器不該代使用者按，也不該把分享頁存成 exe。
                Panel(chosen.BaseUrl);
                Console.WriteLine();
                Ui.Panel(Width(),
                    Ui.BoldMark() + " 手動放置",
                    new[]
                    {
                        "請在瀏覽器按「下載」，把 " + (full ? "XinSpect-full.exe" : "XinSpect.exe") + " 放到：",
                        Ui.Dim + dir + Ui.Reset,
                        "下載完成後把 XinSpect.exe 放進上面那個資料夾就能直接執行。",
                        Ui.Dim + "想全自動部署（含守護進程）請重新執行本程式，選 A 後按 1（GitHub）。" + Ui.Reset,
                    },
                    null);
                Footer();
                Pause();
                return 0;
            }

            string guiRemote = full ? "XinSpect-full.exe" : "XinSpect.exe";
            Ui.Section("1/5 · 下載主程式", guiRemote + "（含 LHM＋WinRing0 驅動＋BlueSquadron）");
            DownloadFileWithFallback(chosen, guiRemote, Path.Combine(dir, "XinSpect.exe"), 1, 5, requireProgram: true, expectSha: RequireHash(hashes, chosen, guiRemote));

            Ui.Section("2/5 · 下載守護進程", "BlueSquadronBridge.exe（獨立守護）");
            DownloadFileWithFallback(chosen, "BlueSquadronBridge.exe", Path.Combine(dir, "BlueSquadronBridge.exe"), 2, 5, requireProgram: true, expectSha: RequireHash(hashes, chosen, "BlueSquadronBridge.exe"));

            // 深層核心存取的驅動：程式按「開啟」會自己簽章＋裝服務，但檔案必須先就位。
            // 3.17 起驅動檔不隨 Release 下發——這一步會如實說「未附」，不會假裝成功。
            Ui.Section("3/5 · 部署核心驅動", "XsRegProbe.sys（深層核心存取）");
            string driverNote = DeployDriver(chosen, dir, hashes);
            Ui.Ok(driverNote);

            Ui.Section("4/5 · 就位", "已放進 Desktop\\XinSpect\\");
            Ui.Section("5/5 · 啟動", "XinSpect.exe");
            Console.WriteLine();
            string exe = Path.Combine(dir, "XinSpect.exe");
            if (File.Exists(exe))
            {
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); Ui.Ok("已啟動 XinSpect.exe"); } catch { }
            }

            Console.WriteLine();
            Ui.Panel(Width(),
                Ui.BoldMark() + " 部署完成",
                new[]
                {
                    Ui.CheckMark() + " XinSpect.exe" + Ui.Dim + "（含 WinRing0＋LHM＋BlueSquadron）" + Ui.Reset,
                    Ui.CheckMark() + " BlueSquadronBridge.exe" + Ui.Dim + "（獨立守護進程）" + Ui.Reset,
                    Ui.CheckMark() + " " + driverNote,
                    Ui.Dim + "位置：" + dir + Ui.Reset,
                },
                null);
            Footer();
            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            return DeployFailed(ex);
        }
    }

    private static int DeployFailed(Exception ex)
    {
        Console.WriteLine();
        Ui.Err("部署失敗：" + ex.Message);
        Console.WriteLine();
        Ui.Panel(Width(),
            Ui.BoldMark() + " 手動下載",
            new[]
            {
                "https://github.com/Xinglanclever/XinSpect/releases",
            },
            null);
        Footer();
        Pause();
        return 1;
    }

    // page 模式的「開瀏覽器」收在它自己的小面板裡——網址是最重要的資訊，給它一行專屬。
    private static void Panel(string url)
    {
        Console.WriteLine();
        Ui.Section("1/1 · 分享頁", "已開瀏覽器，請在頁面上按「下載」");
        Ui.Info("網址", url);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // 部署驅動：下載 XsRegProbe.sys，放進程式指定的固定路徑。
    // 不在這裡簽章、也不裝憑證——那些是程式裡「深層核心存取」開關的工作。
    // 這裡只負責「檔案到位」；遠端沒有這個檔（404）是預期內的缺席，與故障分開說。
    private static string DeployDriver(Mirror m, string appDir, HashList hashes)
    {
        bool elevated = IsElevated();
        string driverDir;
        string target;
        if (elevated)
        {
            driverDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "XinSpect", "Driver");
            target = Path.Combine(driverDir, "XsRegProbe.sys");
            try
            {
                Directory.CreateDirectory(driverDir);
            }
            catch (Exception ex)
            {
                return "XsRegProbe.sys 未部署（無法建立 " + driverDir + "：" + ex.Message + "）";
            }
        }
        else
        {
            // 沒提權：放到程式旁邊，並把「為什麼」講清楚——不假裝部署成功。
            driverDir = Path.Combine(appDir, "Driver");
            Directory.CreateDirectory(driverDir);
            target = Path.Combine(driverDir, "XsRegProbe.sys");
        }

        string tmp = target + ".part";
        try
        {
            DownloadFileWithFallback(m, "XsRegProbe.sys", tmp, 3, 5, requireProgram: false, expectSha: RequireHash(hashes, m, "XsRegProbe.sys"));
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            if (IsNotFound(ex))
                return "XsRegProbe.sys 未部署（此版未附驅動檔）——深層核心存取仍可由文件手冊手動建置";
            return "XsRegProbe.sys 未部署（" + ex.Message + "）——深層核心存取仍可由文件手冊手動建置";
        }

        if (elevated)
            return "XsRegProbe.sys -> " + target;
        return "XsRegProbe.sys -> " + target
             + "（未以管理員執行，未寫入 ProgramData；請把這個檔複製到 "
             + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "XinSpect", "Driver", "XsRegProbe.sys")
             + "，或以管理員身分重新執行本部署器）";
    }

    // 遠端 404（此版未附這個檔）與真正的下載失敗要分開說：前者是預期內的缺席，後者才叫故障。
    private static bool IsNotFound(Exception ex)
    {
        WebException web = ex as WebException;
        if (web == null) return false;
        HttpWebResponse resp = web.Response as HttpWebResponse;
        return resp != null && resp.StatusCode == HttpStatusCode.NotFound;
    }

    private static bool IsElevated()
    {
        try
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private static List<Mirror> LoadMirrors()
    {
        List<Mirror> list = new List<Mirror>();
        list.Add(new Mirror("1", "GitHub Releases（官方，永遠是最新版）", GitHubBase, direct: true));
        list.Add(new Mirror("2", "Google Drive（鏡像分享頁）", GoogleDriveFolderUrl, direct: false));
        // 第 2 格內建走 page 而不是 direct：uc?export=download&id= 是單一檔案端點，
        // 把檔名接上去是壞網址；分享的是資料夾頁。mirrors.txt 若列了真正 direct 的端點會覆寫。
        list.Add(new Mirror("3", "中國加速線路（GitHub 加速節點）", ChinaProxyBase, direct: true));

        string text = FetchMirrorList();
        if (!string.IsNullOrEmpty(text)) ApplyMirrorList(list, text);
        return list;
    }

    // 抓 mirrors.txt；三條線輪流試，全部失敗才用內建值。
    // 這一支不能讓安裝器卡住——鏡像本來就是「GitHub 連不上時」才用的。
    private static string FetchMirrorList()
    {
        foreach (string url in MirrorListUrls)
        {
            string text = FetchText(url, MirrorListMaxBytes);
            if (text != null) return text;
        }
        return null;
    }

    // 有界的清單抓取：https 強制、大小上限、逾時 6 秒。半截回應與超限一律回 null——
    // 寧可回退內建清單，也不讓「清單下載」這條路成為第二個下載面。
    private static string FetchText(string url, int maxBytes)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "XinSpectDeploy/" + Version;
            req.Timeout = 6000;
            req.ReadWriteTimeout = 6000;
            req.Proxy = WebRequest.GetSystemWebProxy();
            req.Proxy.Credentials = CredentialCache.DefaultCredentials;
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            {
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int total = 0, n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > maxBytes) return null;   // 超限＝不是清單，當抓取失敗
                    ms.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch { return null; }
    }

    private sealed class HashList
    {
        public Dictionary<string, string> ByName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Via = "";          // 這份清單是從哪來的（三態標示給使用者看）
        public bool OfficialDirect;      // true＝官方直連取得（信任根）
    }

    // 抓官方總雜湊清單。優先序：GitHub 直連（信任根）→ GhProxies 前綴（信任＝節點營運者，
    // 降級標示）→ 所選鏡像自帶的那份（信任＝鏡像營運者，最低；只用於完整性，不冒充官方）。
    // 全數失敗回 null——呼叫端（RequireHash）據此拒裝並三態告知。
    private static HashList FetchShaSums(Mirror chosen)
    {
        List<string[]> routes = new List<string[]>();
        routes.Add(new string[] { ShaSumsUrl, "官方直連（信任根）", "official" });
        if (chosen.BaseUrl.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) < 0)
        {
            foreach (string p in GhProxies)
                routes.Add(new string[] { p + ShaSumsUrl, "官方經加速節點（信任＝節點營運者）", "proxy" });
            if (chosen.Direct && chosen.BaseUrl.Length > 0)
                routes.Add(new string[] { chosen.Url("SHA256SUMS.txt"), "所選鏡像自帶（信任＝鏡像營運者）", "mirror" });
        }
        foreach (string[] r in routes)
        {
            string text = FetchText(r[0], MirrorListMaxBytes);
            if (text == null) continue;
            HashList hl = ParseShaSums(text);
            if (hl != null) { hl.Via = r[1]; hl.OfficialDirect = (r[2] == "official"); return hl; }
        }
        return null;
    }

    private static HashList ParseShaSums(string text)
    {
        HashList hl = new HashList();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length < 66 || line.Length > 300) continue;
            string hash = line.Substring(0, 64).ToLowerInvariant();
            if (!IsHex(hash)) continue;
            if (line[64] != ' ' && line[64] != '*') continue;
            string name = line.Substring(65).Trim();
            if (name.Length == 0 || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) continue;  // 只認裸檔名
            hl.ByName[name] = hash;
        }
        return hl.ByName.Count > 0 ? hl : null;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return s.Length > 0;
    }

    private static string Sha256File(string path)
    {
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
        using (FileStream fs = File.OpenRead(path))
        {
            byte[] dig = sha.ComputeHash(fs);
            StringBuilder sb = new StringBuilder(dig.Length * 2);
            foreach (byte b in dig) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // 遠端清單解析（v2）：
    //   v2 direct 行：key|url|direct|<64位hex>|<大小>  —— direct 必附 SHA-256，缺了整行拒收
    //   v1 page  行：key|url|page                     —— page 模式只開瀏覽器，仍收（v1 相容）
    //   v1 direct 行：key|url|direct                  —— 無雜湊，拒收（舊檔的 direct 條目需升級，見 patch-22.md 第 4 節）
    // 鐵律：
    //   1. 內建 key 1/2/3 是編譯期信任根——遠端清單**不得覆寫**，撞到就記一筆說明（ spinner 停了再印）。
    //   2. 只收 https（v1 時代連 http 都收——那是 V2 漏洞的一半）。
    //   3. 重複 key 不收（先到先得，三條抓取線誰先回誰算）。
    private static void ApplyMirrorList(List<Mirror> mirrors, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] parts = line.Split('|');
            if (parts.Length < 3) continue;

            string key = parts[0].Trim();
            string url = parts[1].Trim();
            string mode = parts[2].Trim();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

            if (key == "1" || key == "2" || key == "3")
            {
                PendingNotices.Add("mirrors.txt：忽略對內建來源 " + key + " 的覆寫（信任根不可被遠端清單改寫）。");
                continue;
            }
            if (mirrors.Exists(delegate(Mirror m) { return m.Key == key; })) continue;

            bool direct = mode.Equals("direct", StringComparison.OrdinalIgnoreCase);
            Mirror nm = new Mirror(key, "鏡像 " + key + "（來自遠端清單）", url, direct);
            if (direct)
            {
                if (parts.Length >= 4)
                {
                    string sha = parts[3].Trim().ToLowerInvariant();
                    if (sha.Length == 64 && IsHex(sha)) nm.Sha256 = sha;
                }
                if (nm.Sha256 == null)
                {
                    PendingNotices.Add("mirrors.txt：第 " + key + " 格 direct 條目未附 SHA-256（v2 格式），已拒收。");
                    continue;
                }
            }
            mirrors.Add(nm);
        }
    }

    // 選來源：命令列參數優先（第一個非 -xsc 的參數是來源編號，腳本與機器驗證不必有人坐在前面），
    // 沒有參數才進互動選單。編號 1/2/3；A/B 是第一層的產品鍵，不是來源編號。
    private static Mirror ChooseMirror(List<Mirror> all, string[] args)
    {
        string srcArg = null;
        if (args != null)
        {
            foreach (string a in args)
            {
                string t = (a ?? "").Trim();
                if (t.Equals("-xsc", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("--xsc", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("-legacy", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("--legacy", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("-nort", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("--nort", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Length > 0) { srcArg = t; break; }
            }
        }
        if (srcArg != null)
        {
            foreach (Mirror m in all)
            {
                if (m.Key == srcArg && m.BaseUrl.Length > 0) return m;
            }
            Ui.Warn("無法使用來源「" + srcArg + "」（沒有提供或沒有這個編號）。");
            return null;
        }

        int width = Width();
        List<string> rows = new List<string>();
        for (int i = 0; i < all.Count; i++)
        {
            string tag = all[i].Direct ? "direct" : "page";
            rows.Add(Ui.Bold + "[" + all[i].Key + "]" + Ui.Reset + " " + all[i].Name
                   + Ui.Dim + "  " + tag + (all[i].BaseUrl.Length == 0 ? "・尚未提供" : "") + Ui.Reset);
        }
        Console.WriteLine();
        Ui.Panel(width, Ui.BoldMark() + " 選擇下載來源", rows, "Enter＝1 · GitHub 官方");
        Ui.Prompt("按編號選擇（直接按 Enter 用 1，GitHub）：");

        string key = ReadKeyOrEmpty();
        if (key.Length == 0) return all[0];
        key = key.Trim();
        foreach (Mirror m in all)
        {
            if (m.Key == key)
            {
                if (m.BaseUrl.Length > 0) return m;
                Ui.Warn("來源「" + key + "」這版還沒有提供（見上面的「尚未提供」），改用 GitHub。");
                return all[0];
            }
        }

        Ui.Warn("無此選項「" + key + "」，使用第一組來源。");
        return all[0];
    }

    // 下載備援鏈：直連 → 指定版號（latest 404 時）→ 確認直連（Drive）→ 加速節點前綴。
    // 每條線試 2 次；換線時把要換去哪裡說出來——不讓使用者對著靜默的重試發呆。
    private static void DownloadFileWithFallback(Mirror m, string fileName, string dest, int step, int total, bool requireProgram, string expectSha)
    {
        string primary = m.Direct ? m.Url(fileName) : m.BaseUrl;
        List<string> chain = new List<string>();
        if (primary.Length > 0) chain.Add(primary);

        // latest/download 404 時的版號備援：tag 從 GitHub API 現場偵測，不寫死——
        // 寫死的 tag 過兩版就變成「安靜裝到舊版」的陷阱。
        if (primary.Contains("/releases/latest/download/"))
        {
            string tag = LatestTag();
            if (tag.Length > 0)
                chain.Add(primary.Replace("/releases/latest/download/", "/releases/download/" + tag + "/"));
        }

        // Google Drive 對執行檔常常先回「無法掃描病毒」確認頁；usercontent 端點帶 confirm=t 直給檔案。
        string confirm = DriveConfirmUrl(primary);
        if (confirm != null) chain.Add(confirm);

        // 加速節點：對 GitHub 線路整組加前綴（含版號備援那條）。
        if (primary.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0 &&
            primary.IndexOf("ghfast.top", StringComparison.OrdinalIgnoreCase) < 0 &&
            primary.IndexOf("ghproxy", StringComparison.OrdinalIgnoreCase) < 0)
        {
            List<string> via = new List<string>(chain);
            foreach (string proxy in GhProxies)
            {
                foreach (string u in via)
                {
                    chain.Add(proxy + u);
                }
            }
        }

        Exception last = null;
        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0)
            {
                Console.WriteLine();
                Ui.Note("這條線失敗，改用備援線路：" + chain[i]);
            }
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    DownloadFile(chain[i], fileName, dest, step, total, requireProgram, expectSha);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    TryDelete(dest);
                }
            }
        }
        throw last ?? new Exception("下載失敗：所有網路節點均無法完成連線。");
    }

    // 最新版 tag，現場偵測並在整個行程內快取；抓不到就回空字串（不加版號備援，直連與加速節點仍在）。
    // 偵測方式不碰 GitHub API（未認證每小時 60 次，量測常常撞限額），而是看 latest/download
    // 的第一跳 302 Location——那裡就帶 tag，而且 asset 在不在都拿得到。不能跟著重導向走到底：
    // 第二跳轉到 release-assets 的 signed URL，tag 就沒了。
    private static string LatestTag()
    {
        if (_latestTag != null) return _latestTag;
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(GitHubBase + "XinSpect.exe");
            req.UserAgent = "XinSpectDeploy/" + Version;
            req.Method = "HEAD";
            req.AllowAutoRedirect = false;   // 只取第一跳的 Location，見上
            req.Timeout = 6000;
            req.ReadWriteTimeout = 6000;
            req.Proxy = WebRequest.GetSystemWebProxy();
            req.Proxy.Credentials = CredentialCache.DefaultCredentials;
            using (WebResponse resp = req.GetResponse())
            {
                _latestTag = "";
                string location = resp.Headers["Location"];
                const string marker = "/releases/download/";
                if (!string.IsNullOrEmpty(location))
                {
                    int at = location.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        string rest = location.Substring(at + marker.Length);
                        int slash = rest.IndexOf('/');
                        if (slash > 0) _latestTag = rest.Substring(0, slash);
                    }
                }
            }
        }
        catch
        {
            _latestTag = "";
        }
        return _latestTag;
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

    // 一次下載＝一條進度列。自己讀回包而不是用 WebClient：同步 DownloadFile 不觸發進度回呼
    // （上一版掛在這裡），而且能比對 Content-Length 與實收位元組——連線中途斷掉時
    // Stream.Read 只回 0 不丟例外，截短過的 exe 照樣有 MZ 開頭，那是更難查的假成功。
    private static void DownloadFile(string url, string fileName, string dest, int step, int total, bool requireProgram, string expectSha)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ProgressBar bar = RunDownload(url, dest, step, total);

        // 下載到 HTML 是這種安裝器最常見的假成功：分享頁、流量限制頁、病毒掃描警告
        // 都會回 200。這裡驗 PE 標頭＋大小門檻，不合格就刪掉並說清楚。
        if (!LooksLikeDownload(dest, requireProgram))
        {
            long len = 0;
            string head = "";
            try { len = new FileInfo(dest).Length; head = FirstLine(Head(dest, 400)); } catch { }
            TryDelete(dest);
            throw new InvalidDataException(string.Format(
                "{0} 下載回來的不是 Windows 程式（{1:N0} bytes，開頭不是 MZ；內容像是：{2}）。",
                fileName, len, head));
        }

        // 全量 SHA-256：唯一放行條件。MZ＋大小檢查降級為「快失效的診斷訊息」——
        // 它能說清「下載到的是 HTML」，但對惡意 PE 零鑑別力，不再作為放行依據。
        if (expectSha != null)
        {
            string actual = Sha256File(dest);
            if (!actual.Equals(expectSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(dest);
                throw new InvalidDataException(string.Format(
                    "{0} 雜湊不符（預期 {1}，實得 {2}）——下載被改寫或傳輸損壞，已刪除，不會安裝。請改用其他來源或重試。",
                    fileName, expectSha, actual));
            }
        }

        sw.Stop();
        FileInfo fi = new FileInfo(dest);
        double secs = sw.Elapsed.TotalSeconds;
        // 整段的平均速度：和進度列上的即時速度是兩件事，這一條是「從按下到現在」的實測。
        double avg = secs > 0 ? fi.Length / 1048576.0 / secs : 0;
        bar.Overwrite(string.Format("  [{0}/{1}] {2} {3} — {4:N0} bytes in {5:F1}s（{6:0.0} MB/s）",
            step, total, Ui.OkMark(), fileName, fi.Length, secs, avg));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static int Width()
    {
        return Ui.Width();
    }

    /// <summary>
    /// 下載進度列：進度條、即時速度、剩餘時間。
    /// </summary>
    /// <remarks>
    /// 量不到的一律寫「--」，不印一個看起來很肯定的假數字——分塊傳輸沒有
    /// Content-Length，硬湊一個百分比只會讓「卡在 100%」變成另一種誤導。
    /// 畫風與 xsc 對齊：▓░ 填充條帶品牌紅（Fancy 模式）；純文字模式退回 #-
    /// Installer\tests\ProgressTest.cs 以反射驗兩種模式的行寬都不得超過 80 欄。
    /// </remarks>
    public sealed class ProgressBar
    {
        // 條子寬度依實際視窗算：固定 24 格在 80 欄主控台會折行，\r 只回最後一行行首，
        // 每畫一次多一行殘影。寧可短一點，也不要折行。
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
        private int _printedVis;

        public ProgressBar(int step, int total)
        {
            _step = step;
            _total = total;
        }

        private static int BarWidth()
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

        public void Report(long received, long expected)
        {
            long ms = _sw.ElapsedMilliseconds;
            if (_lastMs == 0)
            {
                // 第一坨只記基準點，不算速度：從 0 ms 到 1 ms 之間除出來的數字不是速度，是噪訊。
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
            _printedVis = 0;
        }

        /// <summary>下載失敗時把進度列擦掉，讓下一行的錯誤訊息從乾淨的行首開始。</summary>
        public void Clear()
        {
            if (_printedVis <= 0) return;
            Console.Write("\r" + new string(' ', _printedVis) + "\r");
            _printedVis = 0;
        }

        private void Write(string text)
        {
            // 補白量用「可見寬度」算：Fancy 模式的行裡有 ANSI 序列，字數不等於欄數。
            // 上限取新舊可見寬的較大者——新行較短時要把舊行擦乾淨，不能留殘尾。
            int prev = _printedVis;
            int vis = Ui.VisibleLength(text);
            if (prev > vis) text += new string(' ', prev - vis);
            _printedVis = Math.Max(vis, prev);
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
            int barWidth = BarWidth();
            int fill = (int)(pct * barWidth / 100.0);
            if (fill < 0) fill = 0;
            if (fill > barWidth) fill = barWidth;

            double eta = -1;
            if (_speed > 0 && received < expected) eta = (expected - received) / _speed;

            string bar = Ui.Fancy
                ? Ui.BrandRed + new string('▓', fill) + Ui.Reset + Ui.Dim + new string('░', barWidth - fill) + Ui.Reset
                : new string('#', fill) + new string('-', barWidth - fill);

            return head
                 + "[" + bar + "] "
                 + string.Format("{0,5:0.0}%", pct)
                 + string.Format(" {0,6:0.0}/{1,6:0.0} MB", received / 1048576.0, expected / 1048576.0)
                 + "  " + speed
                 + "  ETA " + Eta(eta);
        }

        private static string Eta(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || seconds > 86400) return "--:--";
            long s = (long)Math.Ceiling(seconds);
            if (s < 3600) return string.Format("{0}:{1:00}", s / 60, s % 60);
            return string.Format("{0}:{1:00}:{2:00}", s / 3600, (s % 3600) / 60, s % 60);
        }
    }

    private static bool LooksLikeDownload(string path, bool requireProgram)
    {
        if (!LooksLikePe(path)) return false;
        // 執行檔門檻 100 KB：幾 KB 的「MZ 開頭」殘片或 stub 不該被當成主程式放行。
        return new FileInfo(path).Length >= (requireProgram ? 1024 * 100 : 1024);
    }

    private static bool LooksLikePe(string path)
    {
        try
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Length < 1024) return false;
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

    // RunDownload 保留原簽名（string, string, int, int）與回傳值：Installer\tests\ProgressTest.cs
    // 以反射叫用它跑「完整／斷線／無 Content-Length」三種真下載，動到簽名等於把驗證拆了。
    private static ProgressBar RunDownload(string url, string dest, int step, int total)
    {
        ProgressBar bar = new ProgressBar(step, total);
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "XinSpectDeploy/" + Version;
            req.AllowAutoRedirect = true;
            req.Timeout = 90000;
            req.ReadWriteTimeout = 90000;
            // 走系統 proxy：公司內網與部分校園網沒有它連 GitHub 根本出不去。
            req.Proxy = WebRequest.GetSystemWebProxy();
            req.Proxy.Credentials = CredentialCache.DefaultCredentials;
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
            bar.Clear();
            throw;
        }
        return bar;
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("  " + Ui.Dim + "按任意鍵關閉…" + Ui.Reset);
        try { Console.ReadKey(intercept: true); } catch { }
    }

    private static void Footer()
    {
        Ui.Footer("曦覽部署器 v" + Version, "曦覽 XinSpect by xinglanclever");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Ui：畫面件。語彙與曦覽終端機的 Tui.cs 同源——瑞士平面、品牌紅、CHA 釘框線。
    // 全部成員都要在 Fancy=false 時給出可讀的純文字等價物。
    // ════════════════════════════════════════════════════════════════════════
    private static class Ui
    {
        // ── ANSI 可用性 ──
        // Windows 10 傳統主控台（conhost）預設不處理 ANSI/VT 序列，所有 ESC[...m、ESC[nG
        // 會原樣印在畫面上（Windows Terminal 與 Win11 預設開啟）。啟動時主動打開
        // ENABLE_VIRTUAL_TERMINAL_PROCESSING；打不開就整體退回純文字（Fancy=false），
        // 不是每個環境都救得動，但不假裝成功。
        public static bool Fancy;

        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static void EnableAnsi()
        {
            if (Console.IsOutputRedirected) return;
            try
            {
                IntPtr h = GetStdHandle(StdOutputHandle);
                if (h == IntPtr.Zero || h == (IntPtr)(-1)) return;
                uint mode;
                if (!GetConsoleMode(h, out mode)) return;
                if (!SetConsoleMode(h, mode | EnableVirtualTerminalProcessing)) return;
                Fancy = true;
            }
            catch { /* 非 Windows 或無主控台：走純文字 */ }
        }

        // ── 色票（屬性而非 const：Fancy=false 一律回空字串，呼叫端寫法不用分流）──
        public static string Reset { get { return Fancy ? "\x1b[0m" : ""; } }
        public static string Bold { get { return Fancy ? "\x1b[1m" : ""; } }
        public static string Dim { get { return Fancy ? "\x1b[90m" : ""; } }
        public static string SwissRed { get { return Fancy ? "\x1b[91m" : ""; } }
        public static string SwissGreen { get { return Fancy ? "\x1b[92m" : ""; } }
        public static string Amber { get { return Fancy ? "\x1b[93m" : ""; } }
        public static string Invert { get { return Fancy ? "\x1b[7m" : ""; } }
        /// <summary>品牌紅 #E30613（truecolor）：框線、⏺、提示符專用，不作內文著色——與 xsc 同規矩。</summary>
        public static string BrandRed { get { return Fancy ? "\x1b[38;2;227;6;19m" : ""; } }

        private const string Esc = "\x1b";
        private static string ClearLine { get { return Fancy ? "\x1b[2K" : ""; } }

        // ── 圖案字元的純文字等價物：所有「長得像圖案」的字元一律過 Mark ──
        public static string Mark(string fancyChar, string asciiChar) { return Fancy ? fancyChar : asciiChar; }
        public static string OkMark() { return BrandRed + Mark("⏺", "*") + Reset; }
        public static string CrossMark() { return SwissRed + Mark("✕", "x") + Reset; }
        public static string CheckMark() { return SwissGreen + Mark("✓", "+") + Reset; }
        public static string Bullet() { return SwissRed + Bold + Mark("■", "*") + Reset; }
        public static string BoldMark() { return Bold + Mark("■", "*") + Reset; }

        // ── 寬度 ──
        /// <summary>可見寬度：剝掉 ANSI escape，CJK／全形字記 2 格。</summary>
        /// <remarks>用 while 而不是 for：for 的 continue 會再跑一次 i++，
        /// 每個 escape 序列後面都會多吃掉一個字元（xsc 的 Tui.VisibleLength 同款陷阱）。</remarks>
        public static int VisibleLength(string s)
        {
            if (s == null) return 0;
            int w = 0;
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\x1b')
                {
                    // ECMA-48：ESC [ 後，參數落在 0x20–0x3F、終止位元組落在 0x40–0x7E。
                    i++;
                    if (i < s.Length && s[i] == '[')
                    {
                        i++;
                        while (i < s.Length && s[i] >= '\u0020' && s[i] <= '\u003f') i++;
                        if (i < s.Length && s[i] >= '\u0040' && s[i] <= '\u007e') i++;
                    }
                    else
                    {
                        while (i < s.Length && s[i] != '\u0007') i++;
                        if (i < s.Length) i++;   // 吃掉 BEL
                    }
                    continue;   // while 的 continue 不會偷跑 i++
                }
                char c = s[i];
                w += char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter
                     || (c >= '\uFF01' && c <= '\uFF60')
                     || (c >= '\u3000' && c <= '\u303F')
                     ? 2 : 1;
                i++;
            }
            return w;
        }

        public static string Pad(string s, int width)
        {
            int gap = width - VisibleLength(s);
            return gap <= 0 ? s : s + new string(' ', gap);
        }

        public static int Width()
        {
            // 下限 58＝雙欄框＋中文說明的最小可用寬；上限 96 免得超寬終端上框線稀稀拉拉。
            try { return Math.Max(58, Math.Min(96, Console.WindowWidth - 1)); }
            catch { return 78; }
        }

        // ── 框線：唯一正解是 CHA 絕對定位（借 xsc Tui 的歷史教訓）──
        // 全形標點、模糊寬度字元、使用者字型回退都會讓「算出來的寬度」與實際渲染差 1–2 格；
        // CHA（ESC[<col>G）把游標釘到絕對欄位，永不折行、永不錯位。
        public static string BoxTop(int width, string title)
        {
            if (!Fancy) return "  " + title + "  " + new string('-', Math.Max(4, width - VisibleLength(title) - 8));
            var sb = new StringBuilder();
            sb.Append(BrandRed).Append('╭').Append(new string('─', Math.Max(1, width - 3)));
            sb.Append(Esc).Append('[').Append(width).Append('G').Append('╮').Append(Reset);
            if (title.Length > 0)
                sb.Append(Esc).Append("[4G").Append(title).Append(Reset);
            return sb.ToString();
        }

        public static string BoxRow(int width, string body)
        {
            if (!Fancy) return "  " + body;
            return BrandRed + "│" + Reset + " " + body + Esc + "[" + width + "G" + BrandRed + "│" + Reset;
        }

        public static string BoxBot(int width, string note)
        {
            if (!Fancy) return note.Length > 0 ? "  " + note : "";
            var sb = new StringBuilder();
            sb.Append(BrandRed).Append('╰').Append(new string('─', Math.Max(1, width - 3)));
            sb.Append(Esc).Append('[').Append(width).Append('G').Append('╯').Append(Reset);
            if (note.Length > 0)
                sb.Append(Esc).Append("[").Append(Math.Max(4, width - VisibleLength(note) - 2)).Append('G')
                  .Append(Dim).Append(note).Append(Reset);
            return sb.ToString();
        }

        public static void Panel(int width, string title, IEnumerable<string> lines, string note)
        {
            Console.WriteLine(BoxTop(width, title));
            foreach (string l in lines)
                Console.WriteLine(BoxRow(width, l == null ? "" : l));
            Console.WriteLine(BoxBot(width, note ?? ""));
        }

        // ── 節區標頭／條目／結果行 ──
        public static void Section(string label, string note)
        {
            string n = note == null ? "" : "  " + Dim + note + Reset;
            Console.WriteLine();
            Console.WriteLine("  " + Bullet() + " " + Bold + label.ToUpperInvariant() + Reset + n);
        }

        /// <summary>細分隔線：點串——用來隔開「來源／目標」與後面的動作區。</summary>
        public static void Rule()
        {
            Console.WriteLine("  " + Dim + new string(Fancy ? '·' : '-', Math.Min(72, Math.Max(8, Width() - 4))) + Reset);
        }

        public static void Item(string key, string text)
        {
            Console.WriteLine("    " + Bold + "[" + key + "]" + Reset + " " + text);
        }

        /// <summary>左標籤右內容的資訊行：標籤補白到 6 格，整齊而不喧嘩。</summary>
        public static void Info(string label, string text)
        {
            string l = label.Length == 0 ? "" : Dim + Pad(label, 6) + Reset + " ";
            Console.WriteLine("  " + l + text);
        }

        public static void Ok(string msg) { Console.WriteLine("  " + OkMark() + " " + msg); }
        public static void Warn(string msg) { Console.WriteLine("  " + Amber + Mark("⚠", "!!") + Reset + " " + msg); }
        public static void Err(string msg) { Console.WriteLine("  " + CrossMark() + " " + msg); }
        public static void Note(string msg) { Console.WriteLine("  " + Dim + msg + Reset); }

        /// <summary>提示符：品牌紅 ＞——與 xsc 的 InputFrame 同款。</summary>
        public static void Prompt(string text)
        {
            Console.Write(BrandRed + Bold + ">" + Reset + " " + text);
        }

        // ── 大字標（ANSI Shadow，借 xsc 的字母；字母間 1 格，全寬 72 欄）──
        private static readonly string[][] BannerLetters =
        {
            new[] { "██╗   ██╗", " ╚██╗ ██╔╝", "  ╚███╔╝ ", " ██╔██╗ ", "╚██╝ ╚██╝" },  // X
            new[] { "██╗", "██║", "██║", "██║", "██║" },                                            // I
            new[] { "███╗   ██╗", "████╗  ██║", "██╔██╗ ██║", "██║╚██╗██║", "██║ ╚████║" },         // N
            new[] { "███████╗", "██╔════╝", "███████╗", "╚════██║", "███████║" },                   // S
            new[] { "██████╗ ", "██╔══██╗", "██████╔╝", "██╔═══╝ ", "██║     " },                   // P
            new[] { "███████╗", "██╔════╝", "█████╗  ", "██╔══╝  ", "███████╗" },                   // E
            new[] { " ██████╗", "██╔════╝", "██║     ", "██║     ", "╚██████╗" },                   // C
            new[] { "████████╗", "╚══██╔══╝", "   ██║   ", "   ██║   ", "   ██║   " },              // T
        };

        /// <summary>字母各列左對齊、補到該字母最寬列——ANSI Shadow 是把整列左對齊畫的，截尾的空格要補回來。</summary>
        private static string[] BannerLetter(int idx)
        {
            string[] rows = BannerLetters[idx];
            int w = 0;
            for (int r = 0; r < rows.Length; r++) if (rows[r].Length > w) w = rows[r].Length;
            string[] outRows = new string[rows.Length];
            for (int r = 0; r < rows.Length; r++) outRows[r] = rows[r] + new string(' ', w - rows[r].Length);
            return outRows;
        }

        private static string[] Banner(int gap)
        {
            string[] rows = new string[5];
            for (int r = 0; r < 5; r++)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(BannerLetter(i)[r]);
                    if (i < 7) sb.Append(new string(' ', gap));
                }
                rows[r] = sb.ToString();
            }
            return rows;
        }

        // ── 開場 ──
        private static readonly string[] Tips =
        {
            "放行只認 SHA256SUMS.txt——MZ 開頭檢查只是診斷訊息，不作為依據",
            "latest/download 404 時現場偵測版號備援，不寫死 tag，避免安靜裝到舊版",
            "下載到 HTML 是安裝器最常見的假成功：分享頁與病毒掃描警告都會回 200",
            "半截的 exe 比沒有檔案更危險：有 MZ 頭、大小也像程式——斷線一律刪檔重來",
            "Drive 的 uc?id= 是單一檔案端點；資料夾分享頁走 page 模式，只開瀏覽器不假裝直連",
        };

        public static void Welcome()
        {
            Console.WriteLine();
            int width = Width();
            string tip = Tips[NewRandom().Next(Tips.Length)];

            if (!Fancy)
            {
                // 純文字開場：零色彩、零框線、零定位。
                Console.WriteLine("  X  I  N  S  P  E  C  T");
                Console.WriteLine("  " + new string('-', Math.Min(72, Math.Max(8, width - 6))));
                Console.WriteLine("  " + Mark("*", "*") + " 曦覽部署器 XinSpect Deploy v" + Version);
                Console.WriteLine("  [A] XinSpect.exe 主程式（GUI，含守護進程與驅動）");
                Console.WriteLine("  [B] XinSpectConsole.exe 曦覽終端機（CLI，唯讀事實查詢）");
                Console.WriteLine("  小知識：" + tip);
                Console.WriteLine("  腳本模式：-xsc -full -legacy -nort <來源編號>");
                Console.WriteLine();
                return;
            }

            // 大字標 XINSPECT（全寬 72；終端不夠寬就退回間隔大寫）
            Console.WriteLine();
            if (width >= 76)
            {
                string[] banner = Banner(1);
                int pad = Math.Max(0, (width - banner[0].Length) / 2);
                foreach (string row in banner)
                    Console.WriteLine(new string(' ', pad) + BrandRed + row + Reset);
                string by = "by: xinglanclever";
                Console.Write(Esc + "[" + (width - by.Length) + "G" + Dim + by + Reset);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("  X  I  N  S  P  E  C  T");
            }

            Panel(width,
                Mark("☼", "*") + " " + Bold + "曦覽部署器" + Reset + " " + Dim + "XinSpect Deploy v" + Version + Reset,
                new[]
                {
                    "",
                    Bold + "部署什麼" + Reset + Dim + " ・ Enter 直接開始" + Reset,
                    Bold + "[A]" + Reset + " XinSpect.exe 主程式（GUI，含守護進程與驅動）",
                    Bold + "[B]" + Reset + " XinSpectConsole.exe 曦覽終端機（CLI，唯讀事實查詢）",
                    "",
                    Dim + "下載鏈：GitHub 官方直連 ＞ 加速節點 ＞ 遠端鏡像清單" + Reset,
                    Dim + "放行條件：SHA256SUMS.txt 全量核對，不符即刪不裝" + Reset,
                    "",
                    Dim + "小知識：" + tip + Reset,
                },
                "腳本模式：-xsc -full -legacy -nort <來源編號>");
            Console.WriteLine();
        }

        // Random 用完即棄（C# 5 沒有 Random.Shared；只取一個小知識，不必長駐種子）。
        private static Random NewRandom() { return new Random(); }

        // ── 底部狀態列：整行反白，左狀態、右署名（借 xsc 的 Footer）──
        public static void Footer(string left, string right)
        {
            int width = Width();
            if (!Fancy)
            {
                Console.WriteLine("  " + new string('-', Math.Min(72, Math.Max(8, width - 6))));
                Console.WriteLine("  " + left + " | " + right);
                return;
            }
            int pad = width - VisibleLength(left) - VisibleLength(right) - 3;
            if (pad < 1) pad = 1;
            Console.WriteLine(Invert + " " + left + new string(' ', pad) + right + " " + Reset);
        }

        // ── Spinner：braille 幀＋經過秒數（借 xsc 的 SparkFrames；純文字版用 ASCII）──
        private static readonly string[] SpinFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private static readonly string[] SpinFramesAscii = { "*", "o", ".", "o" };

        private static Thread _spinThread;
        private static volatile bool _spinRun;
        private static string _spinLabel;
        private static int _spinPad;
        private static readonly Stopwatch SpinClock = new Stopwatch();

        /// <summary>
        /// 開始轉圈：抓鏡像清單、抓信任根這類「不知道要多久、也沒有分母」的等待用。
        /// 有分母的下載走 ProgressBar，不走這裡。重導輸出時不畫動畫，只留 label。
        /// </summary>
        public static void Busy(string label)
        {
            if (_spinThread != null) return;
            _spinLabel = label;
            _spinRun = true;
            _spinPad = 0;
            SpinClock.Restart();
            if (Fancy && !Console.IsOutputRedirected) Console.Write("\x1b[?25l");
            _spinThread = new Thread(SpinLoop);
            _spinThread.IsBackground = true;
            _spinThread.Start();
        }

        private static void SpinLoop()
        {
            bool redirected = Console.IsOutputRedirected;
            int i = 0;
            while (_spinRun)
            {
                double secs = SpinClock.Elapsed.TotalSeconds;
                if (!redirected)
                {
                    string frame = Fancy ? SpinFrames[i++ % SpinFrames.Length] : SpinFramesAscii[i++ % SpinFramesAscii.Length];
                    string line = BrandRed + frame + Reset + " " + BrandRed + _spinLabel + Reset + Dim + "…" + Reset
                                + " " + Bold + string.Format("{0,4:0.0}s", secs) + Reset;
                    int vis = VisibleLength(line);
                    string pad = _spinPad > vis ? new string(' ', _spinPad - vis) : "";
                    _spinPad = Math.Max(vis, _spinPad);
                    Console.Write("\r" + ClearLine + line + pad);
                }
                Thread.Sleep(100);
            }
        }

        /// <summary>靜靜收掉轉圈（不印結果行）。</summary>
        public static void BusyAbort()
        {
            BusyEndCore();
        }

        /// <summary>收掉轉圈並印結果行：ok＝⏺（品牌紅），失敗＝✕（警示紅）。</summary>
        public static void BusyEnd(bool ok, string msg)
        {
            BusyEndCore();
            if (msg == null) return;
            if (ok) Ok(msg); else Err(msg);
        }

        private static void BusyEndCore()
        {
            _spinRun = false;
            Thread t = _spinThread;
            _spinThread = null;
            if (t != null) { try { t.Join(400); } catch { } }
            if (!Console.IsOutputRedirected)
            {
                Console.Write("\r" + ClearLine);
                if (!Fancy && _spinPad > 0) Console.Write(new string(' ', _spinPad) + "\r");
                if (Fancy) Console.Write("\x1b[?25h");
            }
            _spinPad = 0;
        }
    }
}
