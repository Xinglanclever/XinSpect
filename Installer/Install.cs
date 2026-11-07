using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Security.Principal;

internal static class Install
{
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

        Console.WriteLine();
        Console.WriteLine("  ==========================================");
        Console.WriteLine("     XiLan XinSpect Auto Deploy");
        Console.WriteLine("  ==========================================");
        Console.WriteLine();

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
        List<Mirror> all = LoadMirrors();
        Mirror chosen = ChooseMirror(all, args);
        if (chosen == null) { Pause(); return 2; }

        // 官方總雜湊清單：整個部署抓一次、所有檔共用。null＝一份都拿不到——
        // 各 Deploy* 內的 RequireHash 會據此拒裝並三態告知，不在這裡提早退出
        // （page 模式的「開瀏覽器」不需要雜湊，退出會把那條活路也關掉）。
        HashList hashes = FetchShaSums(chosen);
        if (hashes == null)
            Console.WriteLine("  ⚠ 無法取得官方總雜湊清單（SHA256SUMS.txt）——直連下載將被拒絕。");

        return xsc ? DeployXsc(chosen, legacy, nort, hashes) : DeployGui(chosen, full, hashes);
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

        Console.WriteLine("  要安裝什麼：");
        Console.WriteLine();
        Console.WriteLine("    [A] XinSpect.exe 主程式（GUI，含守護進程與驅動）");
        Console.WriteLine("    [B] XinSpectConsole.exe 曦覽終端機（CLI，單檔自含執行階段、唯讀事實查詢）");
        Console.WriteLine();
        Console.Write("  按 A 或 B 選擇（直接按 Enter 預設 A，主程式）：");

        string key = "";
        try
        {
            key = Console.ReadKey(intercept: true).KeyChar.ToString();
            Console.WriteLine(string.IsNullOrEmpty(key) ? "" : " " + key);
        }
        catch { Console.WriteLine(); }
        Console.WriteLine();

        if (string.IsNullOrEmpty(key) || key == "\r" || key == "\n") return false;   // Enter ＝ 主程式
        key = key.Trim().ToUpperInvariant();
        if (key == "B") return true;
        if (key == "A") return false;

        Console.WriteLine("  看不懂「" + key + "」，改裝主程式。");
        Console.WriteLine();
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

        Console.WriteLine("  選版本：");
        Console.WriteLine();
        Console.WriteLine("    [1] 不帶環境（約 36MB；機器要已裝 .NET 10 Desktop Runtime）");
        Console.WriteLine("    [2] 帶環境（免裝 .NET，單檔較大）");
        Console.WriteLine();
        Console.Write("  按 1 或 2（直接按 Enter 用 1）：");

        string key = ReadKeyOrEmpty();
        if (key.Length == 0) return false;   // Enter ＝ 不帶環境（現在版）
        if (key == "2") return true;
        if (key == "1") return false;

        Console.WriteLine("  看不懂「" + key + "」，用不帶環境的現在版。");
        Console.WriteLine();
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

        Console.WriteLine("  選版本：");
        Console.WriteLine();
        Console.WriteLine("    [1] Windows 11／Server 2025・帶環境（免裝 .NET，約 100MB）");
        Console.WriteLine("    [2] Windows 10／2016/2019/2022・帶環境（免裝 .NET，純文字界面，約 100MB）");
        Console.WriteLine("    [3] Windows 11／Server 2025・不帶環境（約 60MB，需 .NET 10 Desktop Runtime）");
        Console.WriteLine("    [4] Windows 10／2016/2019/2022・不帶環境（約 60MB，純文字界面，需 .NET 10 Desktop Runtime）");
        Console.WriteLine();
        Console.Write("  按 1 / 2 / 3 / 4（直接按 Enter 用 1）：");

        string key = ReadKeyOrEmpty();
        if (key.Length == 0) return;                       // Enter ＝ 1
        if (key == "2") { legacy = true; return; }
        if (key == "3") { nort = true; return; }
        if (key == "4") { legacy = true; nort = true; return; }
        if (key == "1") return;

        Console.WriteLine("  看不懂「" + key + "」，用 Windows 11 帶環境版。");
        Console.WriteLine();
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
            string flavor = legacy ? "plain-text legacy build for Windows 10 / Server 2016, 2019, 2022"
                                   : "full UI build for Windows 11 / Server 2025";
            flavor += nort ? "; needs .NET 10 Desktop Runtime, ~60MB"
                           : "; includes .NET 10 runtime, ~100MB";

            Console.WriteLine("  Source : " + chosen.Name);
            Console.WriteLine("  Target : " + dest);
            Console.WriteLine();
            Directory.CreateDirectory(dir);

            if (!chosen.Direct)
            {
                // 分享頁：只開瀏覽器，不假裝自己抓得到。檔名用全名——xsc.exe 已不存在於 Release。
                Console.WriteLine("  [1/1] 已開啟分享頁。請在瀏覽器按「下載」，把 " + remoteName + " 放到：");
                Console.WriteLine("        " + dir);
                Console.WriteLine("        " + chosen.BaseUrl);
                Process.Start(new ProcessStartInfo(chosen.BaseUrl) { UseShellExecute = true });
                Console.WriteLine();
                Console.WriteLine("  ==========================================");
                Console.WriteLine("  下載完成後，把它改名成 XinSpectConsole.exe 放進上面那個資料夾就能直接執行。");
                Console.WriteLine("  ==========================================");
                Console.WriteLine();
                Pause();
                return 0;
            }

            Console.WriteLine("  [1/2] Downloading " + remoteName + " (" + flavor + ")...");
            DownloadFileWithFallback(chosen, remoteName, dest, 1, 2, requireProgram: true, expectSha: RequireHash(hashes, chosen, remoteName));

            Console.WriteLine("  [2/2] Deployed to Desktop\\XinSpect folder.");
            Console.WriteLine();
            Console.WriteLine("  ==========================================");
            Console.WriteLine("  Deploy complete.");
            Console.WriteLine(legacy
                ? "  - XinSpectConsole.exe（曦覽終端機・純文字相容版；read-only hardware facts console）"
                : "  - XinSpectConsole.exe（曦覽終端機；read-only hardware facts console）");
            Console.WriteLine("  Location: Desktop\\XinSpect\\");
            Console.WriteLine("  ==========================================");
            Console.WriteLine();
            Console.Write("  現在就開啟曦覽終端機嗎？按 Y 開啟，其他鍵跳過：");
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
                }
                catch (Exception launchEx)
                {
                    Console.WriteLine();
                    Console.WriteLine("  啟動失敗（" + launchEx.Message + "）。如果是 nort（不帶環境）版本，請先安裝 .NET 10 Desktop Runtime：");
                    Console.WriteLine("    https://dotnet.microsoft.com/download/dotnet/10.0");
                }
            }
            Console.WriteLine();
            Console.WriteLine("  Press any key to close...");
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

            Console.WriteLine("  Source : " + chosen.Name);
            Console.WriteLine("  Target : " + dir);
            Console.WriteLine();
            Directory.CreateDirectory(dir);

            if (!chosen.Direct)
            {
                // 分享頁：只開瀏覽器。網盤的下載要過頁面上的按鈕，有的還要登入或驗證碼——
                // 安裝器不該代使用者按，也不該把分享頁存成 exe。
                Console.WriteLine("  [1/1] 已開啟分享頁。請在瀏覽器按「下載」，把 " + (full ? "XinSpect-full.exe" : "XinSpect.exe") + " 放到：");
                Console.WriteLine("        " + dir);
                Console.WriteLine("        " + chosen.BaseUrl);
                Process.Start(new ProcessStartInfo(chosen.BaseUrl) { UseShellExecute = true });

                Console.WriteLine();
                Console.WriteLine("  ==========================================");
                Console.WriteLine("  下載完成後，把 XinSpect.exe 放進上面那個資料夾就能直接執行。");
                Console.WriteLine("  想全自動部署（含守護進程）請重新執行本程式，選 A 後按 1（GitHub）。");
                Console.WriteLine("  ==========================================");
                Console.WriteLine();
                Pause();
                return 0;
            }

            string guiRemote = full ? "XinSpect-full.exe" : "XinSpect.exe";
            Console.WriteLine("  [1/5] Downloading " + guiRemote + " (includes LHM + WinRing0 driver + BlueSquadron)...");
            DownloadFileWithFallback(chosen, guiRemote, Path.Combine(dir, "XinSpect.exe"), 1, 5, requireProgram: true, expectSha: RequireHash(hashes, chosen, guiRemote));

            Console.WriteLine("  [2/5] Downloading BlueSquadronBridge.exe...");
            DownloadFileWithFallback(chosen, "BlueSquadronBridge.exe", Path.Combine(dir, "BlueSquadronBridge.exe"), 2, 5, requireProgram: true, expectSha: RequireHash(hashes, chosen, "BlueSquadronBridge.exe"));

            // 深層核心存取的驅動：程式按「開啟」會自己簽章＋裝服務，但檔案必須先就位。
            // 3.17 起驅動檔不隨 Release 下發——這一步會如實說「未附」，不會假裝成功。
            Console.WriteLine("  [3/5] Deploying XsRegProbe.sys (deep kernel access driver)...");
            string driverNote = DeployDriver(chosen, dir, hashes);

            Console.WriteLine("  [4/5] Deployed to Desktop\\XinSpect folder.");

            Console.WriteLine("  [5/5] Launching XinSpect...");
            Console.WriteLine();
            string exe = Path.Combine(dir, "XinSpect.exe");
            if (File.Exists(exe))
            {
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { }
            }

            Console.WriteLine("  ==========================================");
            Console.WriteLine("  Deploy complete.");
            Console.WriteLine("  - XinSpect.exe (includes WinRing0 + LHM + BlueSquadron)");
            Console.WriteLine("  - BlueSquadronBridge.exe (standalone guard)");
            Console.WriteLine("  - " + driverNote);
            Console.WriteLine("  Location: Desktop\\XinSpect\\");
            Console.WriteLine("  ==========================================");
            Console.WriteLine();
            Console.WriteLine("  Press any key to close...");
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
        Console.WriteLine("  Deploy failed: " + ex.Message);
        Console.WriteLine();
        Console.WriteLine("  Manual download:");
        Console.WriteLine("    https://github.com/Xinglanclever/XinSpect/releases");
        Console.WriteLine();
        Console.WriteLine("  Press any key to close...");
        Pause();
        return 1;
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
            req.UserAgent = "XinSpectDeploy/3.18";
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
        public System.Collections.Generic.Dictionary<string, string> ByName =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    //   1. 內建 key 1/2/3 是編譯期信任根——遠端清單**不得覆寫**，撞到就忽略並印一行說明。
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
                Console.WriteLine("  mirrors.txt：忽略對內建來源 " + key + " 的覆寫（信任根不可被遠端清單改寫）。");
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
                    Console.WriteLine("  mirrors.txt：第 " + key + " 格 direct 條目未附 SHA-256（v2 格式），已拒收。");
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
            Console.WriteLine("  無法使用來源「" + srcArg + "」（沒有提供或沒有這個編號）。");
            Console.WriteLine();
            return null;
        }

        Console.WriteLine("  選擇下載來源：");
        Console.WriteLine();
        for (int i = 0; i < all.Count; i++)
        {
            Console.WriteLine("    [" + all[i].Key + "] " + all[i].Name +
                              (all[i].BaseUrl.Length == 0 ? "  —— 尚未提供" : ""));
        }
        Console.WriteLine();
        Console.Write("  按 1 / 2 / 3 選擇（直接按 Enter 用 1，GitHub）：");

        string key = "";
        try
        {
            key = Console.ReadKey(intercept: true).KeyChar.ToString();
            Console.WriteLine(string.IsNullOrEmpty(key) ? "" : " " + key);
        }
        catch { Console.WriteLine(); }
        Console.WriteLine();

        if (string.IsNullOrEmpty(key) || key == "\r" || key == "\n") return all[0];
        key = key.Trim();
        foreach (Mirror m in all)
        {
            if (m.Key == key)
            {
                if (m.BaseUrl.Length > 0) return m;
                Console.WriteLine("  來源「" + key + "」這版還沒有提供（見上面的「尚未提供」），改用 GitHub。");
                Console.WriteLine();
                return all[0];
            }
        }

        Console.WriteLine("  無此選項「" + key + "」，使用第一組來源。");
        Console.WriteLine();
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
                Console.WriteLine("  這條線失敗，改用備援線路：" + chain[i]);
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
            req.UserAgent = "XinSpectDeploy/3.18";
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
        bar.Overwrite(string.Format(
            "  [{0}/{1}] Done: {2:N0} bytes in {3:F1}s ({4:0.0} MB/s)",
            step, total, fi.Length, secs, avg));
    }

    private static ProgressBar RunDownload(string url, string dest, int step, int total)
    {
        ProgressBar bar = new ProgressBar(step, total);
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "XinSpectDeploy/3.18";
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// 下載進度列：進度條、即時速度、剩餘時間。
    /// </summary>
    /// <remarks>
    /// 量不到的一律寫「--」，不印一個看起來很肯定的假數字——分塊傳輸沒有
    /// Content-Length，硬湊一個百分比只會讓「卡在 100%」變成另一種誤導。
    /// </remarks>
    private sealed class ProgressBar
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
        private int _printed;

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
            _printed = 0;
        }

        /// <summary>下載失敗時把進度列擦掉，讓下一行的錯誤訊息從乾淨的行首開始。</summary>
        public void Clear()
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
            int barWidth = BarWidth();
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

    private static void Pause()
    {
        try { Console.ReadKey(intercept: true); } catch { }
    }
}
