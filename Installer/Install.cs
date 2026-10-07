using System;
using System.Diagnostics;
using System.IO;
using System.Net;

internal static class Install
{
    static int Main()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        Console.WriteLine();
        Console.WriteLine("  ==========================================");
        Console.WriteLine("     XiLan XinSpect Auto Deploy");
        Console.WriteLine("  ==========================================");
        Console.WriteLine();

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

        string base_url = "https://github.com/Xinglanclever/XinSpect/releases/latest/download/";
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string dir = Path.Combine(desktop, "XinSpect");

        Console.WriteLine("  Source : GitHub Releases (latest)");
        Console.WriteLine("  Target : " + dir);
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(dir);

            // 1) XinSpect.exe (includes LibreHardwareMonitor + WinRing0 + BlueSquadron embedded)
            Console.WriteLine("  [1/4] Downloading XinSpect.exe (includes LHM + WinRing0 driver + BlueSquadron)...");
            DownloadFile(base_url + "XinSpect.exe", Path.Combine(dir, "XinSpect.exe"), 1, 4);

            // 2) BlueSquadronBridge.exe (standalone guard process)
            Console.WriteLine("  [2/4] Downloading BlueSquadronBridge.exe...");
            DownloadFile(base_url + "BlueSquadronBridge.exe", Path.Combine(dir, "BlueSquadronBridge.exe"), 2, 4);

            // 3) Deploy done
            Console.WriteLine("  [3/4] Deployed to Desktop\\XinSpect folder.");

            // 4) Launch
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
            Console.Write("  Press any key to close...");
            try { Console.ReadKey(true); } catch { }
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
            Console.Write("  Press any key to close...");
            try { Console.ReadKey(true); } catch { }
            return 1;
        }
    }

    static void DownloadFile(string url, string dest, int step, int total)
    {
        var sw = Stopwatch.StartNew();
        using (var wc = new WebClient())
        {
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
        var fi = new FileInfo(dest);
        Console.WriteLine(string.Format("\r  [{2}/{3}] Done: {0:N0} bytes ({1:F1}s)   ",
            fi.Length, sw.Elapsed.TotalSeconds, step, total));
    }
}
