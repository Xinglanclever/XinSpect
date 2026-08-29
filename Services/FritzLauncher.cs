using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace XinSpect;

/// <summary>
/// 將內嵌的原版 Fritz Chess Benchmark（xiangqi.exe，單一自含執行檔）解壓到暫存目錄後啟動，
/// 供使用者取得原生 16 執行緒對照分數（× 與 kN/s）。單純轉交，無自動化操作。
/// </summary>
/// <remarks>
/// 暫存目錄是同一使用者可寫的位置，「大小相符」不足以證明既有檔案未被竄改——
/// 沿用前一律以 SHA256 對內嵌正本驗證，驗不過就重寫（被鎖定時改用帶 PID 的新檔名）。
/// </remarks>
public static class FritzLauncher
{
    private const string ResourceName = "XinSpect.Fritz.xiangqi.exe";

    public static string Launch()
    {
        var asm = typeof(FritzLauncher).Assembly;
        if (asm.GetManifestResourceStream(ResourceName) is null)
            return "找不到內建的原版 Fritz 檔案（未內嵌）。";

        string expectedHash;
        using (var hs = asm.GetManifestResourceStream(ResourceName)!)
            expectedHash = FileHash.Of(hs);

        string dir = Path.Combine(Path.GetTempPath(), "XinSpectFritz");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "Fritz_Chess_Benchmark.exe");

        // 既有檔驗得過就沿用，避免重複寫檔（也可能因執行中而鎖定）；驗不過才重寫
        if (FileHash.Of(exe) != expectedHash)
        {
            try
            {
                using var fs = new FileStream(exe, FileMode.Create, FileAccess.Write, FileShare.None);
                using var rs = asm.GetManifestResourceStream(ResourceName)!;
                rs.CopyTo(fs);
            }
            catch (IOException)
            {
                // 舊實例仍在執行而鎖檔：改用帶時間標記的新檔名
                exe = Path.Combine(dir, $"Fritz_{Environment.ProcessId}.exe");
                using var fs = new FileStream(exe, FileMode.Create, FileAccess.Write, FileShare.None);
                using var rs = asm.GetManifestResourceStream(ResourceName)!;
                rs.CopyTo(fs);
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = dir,
            UseShellExecute = true,
        });
        return "已啟動原版 Fritz Chess Benchmark（請於其視窗按開始，預設 16 執行緒）。";
    }
}
