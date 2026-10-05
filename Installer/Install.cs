using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

internal static class Install
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    static int Main()
    {
        AttachConsole(-1);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        Console.WriteLine();
        Console.WriteLine("  ==========================================");
        Console.WriteLine("     XiLan XinSpect Auto Deploy");
        Console.WriteLine("  ==========================================");
        Console.WriteLine();

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

        string url = "https://github.com/Xinglanclever/XinSpect/releases/latest/download/XinSpect.exe";
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string dir = Path.Combine(desktop, "XinSpect");
        string dest = Path.Combine(dir, "XinSpect.exe");

        Console.WriteLine("  Source : GitHub Releases (latest)");
        Console.WriteLine("  Target : " + dest);
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(dir);

            Console.WriteLine("  [1/3] Downloading...");
            var sw = Stopwatch.StartNew();
            using (var wc = new WebClient())
            {
                wc.DownloadProgressChanged += (s, e) =>
                {
                    Console.Write(string.Format("\r  [1/3] {0}%  ({1} / {2} MB)   ",
                        e.ProgressPercentage,
                        e.BytesReceived / 1048576,
                        e.TotalBytesToReceive / 1048576));
                };
                wc.DownloadFile(new Uri(url), dest);
            }
            sw.Stop();
            Console.WriteLine(string.Format("\r  [1/3] Downloaded: {0:N0} bytes ({1:F1}s)   ",
                new FileInfo(dest).Length, sw.Elapsed.TotalSeconds));

            Console.WriteLine("  [2/3] Deploying to Desktop\\XinSpect...");

            Console.WriteLine("  [3/3] Launching XinSpect...");
            Console.WriteLine();
            Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });

            Console.WriteLine("  ==========================================");
            Console.WriteLine("  Done. XinSpect is in Desktop\\XinSpect folder.");
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
}
