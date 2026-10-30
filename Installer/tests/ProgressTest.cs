// 進度列的驗證程式：用反射把 XinSpectDeploy.exe 裡的 ProgressBar 叫出來跑一遍。
//
// 為什麼不重寫一份同樣的邏輯來測：重寫的那份再怎麼測都測不到出貨的那一份。
// 這裡直接載入編譯後的 exe、取得私有的巢狀類別、餵它一串假的位元組數，
// 然後檢查每一行印出來的寬度與內容。
//
// 用法（在 repo 根，改完 Install.cs 先跑 Tools\build-installer.ps1）：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:%TEMP%\progtest.exe Installer\tests\ProgressTest.cs
//   %TEMP%\progtest.exe Installer\XinSpectDeploy.exe
//
// 它載入的是「編譯後的那支 exe」，不是原始碼：所以它同時在驗「出貨的版本」與
// 「Install.cs 改了有沒有重編」。全程用反射，不需要把安裝器的程式碼編進測試專案
// （那會多一個 Main，和 xunit 的進入點打架）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static class ProgressTest
{
    private static int failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) failures++;
    }

    private static int Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : @"Installer\XinSpectDeploy.exe";
        Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
        Type barType = asm.GetType("Install+ProgressBar", true);
        Type installType = asm.GetType("Install", true);

        Console.WriteLine("== 形態 ==");
        Check(barType != null, "找得到 Install+ProgressBar");
        Check(installType != null, "找得到 Install");

        // ── 以反射呼叫私有的 Report / Line ──────────────────────────────
        object bar = Activator.CreateInstance(barType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { 1, 4 }, null);
        MethodInfo report = barType.GetMethod("Report", BindingFlags.Instance | BindingFlags.Public);
        MethodInfo line = barType.GetMethod("Line", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(report != null, "有 Report(long,long)");
        Check(line != null, "有 Line(long,long)");

        // 把視窗釘成 80 欄，讓「不得折行」這條斷言有固定基準。不釘的話，
        // 在 120 欄的終端上量到 82 字不是 bug，是我們量錯了對象——進度列本來就會
        // 跟著視窗長大。輸出被重導向時 SetWindowSize 會丟例外，那時 ProgressBar
        // 自己也會退回 80，兩條路都落在同一個基準上。
        try { Console.SetWindowSize(80, Math.Min(Console.WindowHeight, 25)); } catch { }

        Console.WriteLine();
        Console.WriteLine("== 行寬（模擬 80 欄主控台）==");

        long total = 38000000;   // 約 36 MB，和實際 XinSpect.exe 同級
        long[] marks = { 0, 1000000, 9500000, 19000000, 37999999, 38000000 };
        int maxLen = 0;
        foreach (long got in marks)
        {
            string text = (string)line.Invoke(bar, new object[] { got, total });
            maxLen = Math.Max(maxLen, text.Length);
            Console.WriteLine("  " + text);
            if (text.Length > 80)
                Check(false, "行寬 " + text.Length + " > 80（會折行，\\r 只能回到最後一行）");
        }
        Check(maxLen <= 80, "最寬一行 = " + maxLen + " 欄（<= 80）");

        // ── 沒有 Content-Length 的情況 ────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("== 沒有 Content-Length ==");
        string unknown = (string)line.Invoke(bar, new object[] { 12345678L, 0L });
        Console.WriteLine("  " + unknown);
        Check(unknown.IndexOf("--:--") >= 0, "ETA 誠實寫 --:--");
        Check(unknown.IndexOf("%") < 0, "沒有分母時不編百分比");

        // ── Report 不應該爆；而且跑一段真的時間之後，速度與 ETA 要出現 ──────
        // 注意：這裡刻意讓它真的跑掉一些時間（不是全部塞在同一個毫秒），因為
        // 速度是「取樣區間內的位元組 ÷ 區間時間」，時間沒動就永遠除不出東西。
        Console.WriteLine();
        Console.WriteLine("== Report：速度與 ETA ==");
        object bar2 = Activator.CreateInstance(barType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { 2, 4 }, null);
        MethodInfo report2 = barType.GetMethod("Report", BindingFlags.Instance | BindingFlags.Public);

        long chunk = 512 * 1024;             // 模擬每 512 KB 回呼一次
        long sent = 0;
        bool threw = false;
        try
        {
            while (sent < total)
            {
                sent += chunk;
                report2.Invoke(bar2, new object[] { Math.Min(sent, total), total });
                // 每個 512 KB 停 20 ms ≈ 25 MB/s，整段約 1.5 秒。一定要跨過
                // ProgressBar 的 400 ms 取樣窗：它寧可先寫「--」也不拿 1 毫秒的
                // 區間去乘出一個好看的速度，所以餵太快就永遠看不到速度。
                System.Threading.Thread.Sleep(20);
            }
        }
        catch (Exception)
        {
            threw = true;
        }
        Check(!threw, "Report 全程沒有例外");

        // 跑完之後把最後那一行抓出來：此時應該已經有速度與 ETA
        string last = (string)line.Invoke(bar2, new object[] { total / 2, total });
        Console.WriteLine("  " + last);
        Check(last.IndexOf("-- MB/s") < 0, "有速度值（不是 --）");
        Check(last.IndexOf("ETA --:--") < 0, "有 ETA（不是 --:--）");
        Check(last.Length <= 80, "有速度/ETA 時行寬仍 <= 80（實際 " + last.Length + "）");

        Console.WriteLine();
        Console.WriteLine("== 真下載：RunDownload ==");
        RunDownloadChecks(installType, barType);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ── 真的起一個 HTTP 伺服器，讓 RunDownload 跑完整條路 ───────────────
    // 這一節要驗的是三件事，全部都不能只靠讀程式碼相信：
    //   1. 進度列真的會被畫出來（上一版栽在 WebClient 的同步下載根本不回呼進度）；
    //   2. Content-Length 對得上時不會誤判；
    //   3. 伺服器中途斷線時會丟例外並刪掉半截檔案——否則截減的 exe 有 MZ 頭、
    //      會過 LooksLikeProgram，那是比「下載失敗」更難查的假成功。
    private static void RunDownloadChecks(Type installType, Type barType)
    {
        MethodInfo run = installType.GetMethod("RunDownload",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(run != null, "找得到 RunDownload");
        if (run == null) return;

        int port = StartServer();
        Check(port > 0, "本地測試伺服器已啟動");
        if (port <= 0) return;

        string tmp = Path.Combine(Path.GetTempPath(), "xinspect-progress-test");
        Directory.CreateDirectory(tmp);

        // ① 完整下載
        string full = Path.Combine(tmp, "full.bin");
        try
        {
            object b = run.Invoke(null, new object[] {
                "http://127.0.0.1:" + port + "/full", full, 1, 4 });
        }
        catch (Exception e)
        {
            Check(false, "完整下載丟了例外：" + Inner(e));
        }
        Check(File.Exists(full), "完整下載留下檔案");
        if (File.Exists(full))
            Check(new FileInfo(full).Length == 6000000,
                  "大小 = 6,000,000（實際 " + new FileInfo(full).Length + "）");

        // ② 伺服器只回一半：必須丟例外，而且不能留檔
        string half = Path.Combine(tmp, "half.bin");
        string error = null;
        try
        {
            run.Invoke(null, new object[] {
                "http://127.0.0.1:" + port + "/half", half, 2, 4 });
        }
        catch (TargetInvocationException tie)
        {
            error = tie.InnerException.GetType().Name + ": " + tie.InnerException.Message;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
        }
        Check(error != null, "斷線有丟例外（" + (error ?? "沒丟") + "）");
        Check(!File.Exists(half), "斷線後不留半截檔案");

        // ③ 沒有 Content-Length：要能收完，不能因為沒分母就當失敗
        string chunked = Path.Combine(tmp, "chunked.bin");
        try
        {
            run.Invoke(null, new object[] {
                "http://127.0.0.1:" + port + "/chunked", chunked, 3, 4 });
            Check(File.Exists(chunked), "無 Content-Length 也收得到檔");
        }
        catch (Exception e)
        {
            Check(false, "無 Content-Length 下載失敗：" + Inner(e));
        }

        try { Directory.Delete(tmp, true); } catch { }
    }

    private static string Inner(Exception e)
    {
        Exception t = e is TargetInvocationException && e.InnerException != null
            ? e.InnerException : e;
        return t.GetType().Name + ": " + t.Message;
    }

    private static int StartServer()
    {
        // 用 .NET 的 HttpListener 起一個本機測試伺服器。比再拉一個 Python 行程好控制，
        // 也才能精確做出「只回一半就斷線」這種真實世界最常見的壞情況。
        for (int p = 18080; p < 18120; p++)
        {
            try
            {
                var srv = new TestServer(p);
                srv.Start();
                return p;
            }
            catch
            {
                // 這個 port 被佔用，試下一個
            }
        }
        return 0;
    }

    /// <summary>三個端點：/full 正常、/half 回一半就斷、/chunked 不給 Content-Length。</summary>
    private sealed class TestServer
    {
        private const int Size = 6000000;
        private readonly System.Net.HttpListener _l;
        private readonly int _port;

        public TestServer(int port)
        {
            _port = port;
            _l = new System.Net.HttpListener();
            _l.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        public void Start()
        {
            _l.Start();
            System.Threading.Thread t = new System.Threading.Thread(Loop);
            t.IsBackground = true;
            t.Start();
        }

        private void Loop()
        {
            while (_l.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = _l.GetContext(); }
                catch { return; }

                try
                {
                    string path = ctx.Request.Url.AbsolutePath;
                    byte[] buf = new byte[65536];
                    for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i & 0xFF);

                    if (path == "/chunked")
                    {
                        // 不設 ContentLength64：HttpListener 會用 chunked，client 收不到長度
                        ctx.Response.SendChunked = true;
                    }
                    else
                    {
                        ctx.Response.ContentLength64 = Size;
                    }

                    System.IO.Stream os = ctx.Response.OutputStream;
                    long sent = 0;
                    long limit = path == "/half" ? Size / 2 : Size;
                    while (sent < limit)
                    {
                        int n = (int)Math.Min(buf.Length, limit - sent);
                        os.Write(buf, 0, n);
                        os.Flush();
                        sent += n;
                    }
                    if (path == "/half")
                    {
                        // 直接關掉連線，模擬傳到一半斷線
                        try { ctx.Response.Abort(); } catch { }
                    }
                    else
                    {
                        os.Close();
                    }
                }
                catch { }
            }
        }
    }
}
