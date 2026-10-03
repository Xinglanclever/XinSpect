using System.IO;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>一個載入中的核心模組。<see cref="SignatureValid"/> 為 null＝路徑在 \Windows\ 下、未做逐檔驗證（簽章面由 DriverAudit 涵蓋）。</summary>
public sealed record KernelModuleEntry(string Path, bool? SignatureValid, string SignatureNote);

/// <summary>
/// WP14 核心模組面：目前載入的核心模組清單（psapi EnumDeviceDrivers，usermode 唯讀）。
/// \Windows\ 下的模組只計數——它們的簽章面由 DriverAuditService 的 Win32_PnPSignedDriver
/// （驅動存放區記錄）涵蓋，不重複驗；<b>非系統目錄</b>的載入模組才逐檔跑 Authenticode
/// （wintrust DRIVER_ACTION_VERIFY）——這是「誰在核心裡」最值得追問的那一小撮，
/// 結果如實帶原始錯誤碼不解讀。讀不到如實三態。
/// </summary>
public static class KernelModuleService
{
    private const string Category = "系統與軟體";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<KernelModuleEntry>?>? probe = null)
    {
        var entries = (probe ?? FetchLoadedModules)();
        if (entries is null)
            return Unavailable(at, "EnumDeviceDrivers 列舉失敗——核心模組清單讀不到就是不猜");

        var nonWin = entries.Where(e => !IsWindowsDirectory(e.Path)).ToList();
        string nonWinValue = nonWin.Count == 0
            ? "0 個（載入模組全部在 \\Windows\\ 下）"
            : $"{nonWin.Count} 個；例如 " + string.Join("、", nonWin.Take(3).Select(e => BaseName(e.Path))) +
              (nonWin.Count > 3 ? " 等" : "");

        var failed = nonWin.Where(e => e.SignatureValid is false).ToList();
        var unverifiable = nonWin.Where(e => e.SignatureValid is null).ToList();
        string sigValue = nonWin.Count == 0
            ? "—（沒有需要驗證的模組）"
            : failed.Count == 0 && unverifiable.Count == 0
                ? $"送驗 {nonWin.Count} 個均通過 Authenticode"
                : string.Join("；",
                    new[]
                    {
                        failed.Count > 0
                            ? $"{failed.Count}/{nonWin.Count} 未通過：" + string.Join("、",
                                failed.Take(3).Select(e => $"{BaseName(e.Path)}（{e.SignatureNote}）"))
                            : null,
                        unverifiable.Count > 0
                            ? $"無法驗證：{string.Join("、",
                                unverifiable.Take(3).Select(e => $"{BaseName(e.Path)}（{e.SignatureNote}）"))}"
                            : null,
                    }.Where(s => s is not null).Cast<string>().ToArray());

        return
        [
            Fact("kmod.total", "載入中的核心模組", entries.Count.ToString(), "", at, entries.Count),
            Fact("kmod.nonwindows", "非系統目錄的載入模組", nonWinValue, "執行檔不在 \\Windows\\ 下——常駐核心的第三方面", at, nonWin.Count),
            Fact("kmod.nonwindows.sig", "非系統模組 Authenticode", sigValue, "wintrust DRIVER_ACTION_VERIFY；失敗如實帶原始碼", at, nonWin.Count(f => f.SignatureValid is false)),
        ];
    }

    /// <summary>執行檔是否落在 Windows 目錄（大小寫不敏感；空路徑如實回 false）。</summary>
    public static bool IsWindowsDirectory(string path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>列舉載入中的核心模組並對非系統目錄者做 Authenticode；列舉失敗回 null。</summary>
    public static IReadOnlyList<KernelModuleEntry>? FetchLoadedModules()
    {
        try
        {
            IntPtr[] buffer = new IntPtr[1024];
            if (!EnumDeviceDrivers(buffer, (uint)(buffer.Length * IntPtr.Size), out uint needed))
                return null;
            int count = Math.Min((int)(needed / (uint)IntPtr.Size), buffer.Length);
            var entries = new List<KernelModuleEntry>(count);
            for (int i = 0; i < count; i++)
            {
                string path = GetDriverPath(buffer[i]);
                if (string.IsNullOrEmpty(path)) continue;
                if (IsWindowsDirectory(path))
                {
                    entries.Add(new KernelModuleEntry(path, null, ""));
                    continue;
                }
                var (ok, note) = VerifyAuthenticode(path);
                entries.Add(new KernelModuleEntry(path, ok, note));
            }
            return entries;
        }
        catch { return null; }
    }

    private static string GetDriverPath(IntPtr driver)
    {
        var sb = new System.Text.StringBuilder(1024);
        return GetDeviceDriverFileName(driver, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Authenticode 驗證（wintrust，DRIVER_ACTION_VERIFY）。回（是否通過，原始說明）。</summary>
    private static (bool? Ok, string Note) VerifyAuthenticode(string filePath)
    {
        if (!File.Exists(filePath)) return (null, "檔案不存在——無法驗證");
        var fileInfo = new WintrustFileInfo { CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), PcwszFilePath = filePath };
        var data = new WintrustData
        {
            CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
            DwUIChoice = 2,               // WTD_UI_NONE
            DwUnionChoice = 1,            // WTD_CHOICE_FILE
            PFile = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo)),
        };
        try
        {
            Marshal.StructureToPtr(fileInfo, data.PFile, false);
            Guid actionId = DriverActionVerifyGuid;
            int rc = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
            if (rc == 0) return (true, "");
            return (false, $"0x{rc:X8}");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(data.PFile);
        }
    }

    private static readonly Guid DriverActionVerify = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
    private static Guid DriverActionVerifyGuid => DriverActionVerify;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public string PcwszFilePath;
        public IntPtr HFile;
        public IntPtr PgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PPolicyCallbackData;
        public IntPtr PSipClientData;
        public uint DwUIChoice;
        public uint FdwRevocationChecks;
        public uint DwUnionChoice;
        public IntPtr PFile;
        public uint DwStateAction;
        public IntPtr HWVTStateData;
        public IntPtr PwszURLReference;
        public uint DwProvFlags;
        public uint DwUIContext;
        public IntPtr PSignatureSettings;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid actionId, ref WintrustData data);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers(IntPtr[] drivers, uint bufferSize, out uint needed);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDeviceDriverFileName(IntPtr driver, System.Text.StringBuilder name, int size);

    private static string BaseName(string path) => Path.GetFileName(path);

    private static HardwareFact Fact(string key, string name, string value, string note,
        DateTimeOffset at, double? numeric)
    {
        string v = string.IsNullOrEmpty(note) ? value : $"{value}（{note}）";
        return new HardwareFact(key, Category, name, v, "", "psapi EnumDeviceDrivers＋wintrust Authenticode",
            FactTrustLevel.Measured, false, at, numeric);
    }

    private static IReadOnlyList<HardwareFact> Unavailable(DateTimeOffset at, string reason) =>
        new[] { "kmod.total", "kmod.nonwindows", "kmod.nonwindows.sig" }.Select(key =>
            new HardwareFact(key, Category, key switch
            {
                "kmod.total" => "載入中的核心模組",
                "kmod.nonwindows" => "非系統目錄的載入模組",
                _ => "非系統模組 Authenticode",
            }, "", "", "psapi EnumDeviceDrivers＋wintrust Authenticode",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason)).ToList();
}
