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

    /// <summary>
    /// 一般檔案（使用者模式 DLL／EXE）的 Authenticode 驗證：WINTRUST_ACTION_GENERIC_VERIFY_V2。
    /// 供自我完整性組的依賴稽核（IN-007）重用——同一支 wintrust 呼叫，換一個動作 GUID，
    /// 不為了「驗 DLL」再寫第二份 P/Invoke。回（是否通過，原始說明）。
    /// </summary>
    public static (bool? Ok, string Note) VerifyGenericAuthenticode(string filePath)
        => VerifyCached(GenericVerifyV2, filePath);

    /// <summary>Authenticode 驗證（wintrust，DRIVER_ACTION_VERIFY）。回（是否通過，原始說明）。</summary>
    private static (bool? Ok, string Note) VerifyAuthenticode(string filePath)
        => VerifyCached(DriverActionVerify, filePath);

    /// <summary>驗證結果快取上限；超過就整批清空（有界，不做 LRU 複雜度）。</summary>
    private const int VerdictCacheMax = 2048;

    /// <summary>
    /// Authenticode 結果快取：鍵是<b>檔案指紋</b>（完整路徑＋大小＋最後寫入時間＋動作 GUID），不是時間。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>為什麼要有它（實測）</b>：一次證據掃描裡同一支映像會被重複驗證——監聽埠每個 (協定, 埠)
    /// 各驗一次，而同一支 svchost.exe 往往同時持有好幾個埠；跨輪重載（「更新」）更是每次都從頭驗一遍。
    /// 本機（Server 2025）實測：模組面 1651 ms → 16 ms、監聽埠面 1736 ms → 29 ms（同一支二進位冷／暖對照）；
    /// 真實 usermode 更新路徑（冷快取起算、三次取中位）<b>12,802 ms → 7,747 ms（省 39%）</b>，穩定事實鍵集合逐鍵相同。
    /// </para>
    /// <para>
    /// <b>誠實界線</b>：以檔案指紋失效而非以時間失效——檔案一旦被換掉（大小或最後寫入時間改變）就一定重驗；
    /// 快取只省下重複的驗證，不改任何判定（命中時回傳的 (通過?, 說明) 與不快取時逐字相同）。
    /// 上限 <see cref="VerdictCacheMax"/> 筆，超過整批清空，記憶體有界。
    /// </para>
    /// <para>
    /// <b>診斷閘</b>：環境變數 <c>XINSPECT_SIGCACHE=off</c> 時<b>完全不經快取</b>（不讀也不寫，
    /// 每次都真的送驗）——回傳值與平常逐字相同，用來把「快取效益」與「環境快慢」分開量；
    /// 前面那組前後對照就是這樣量出來的。
    /// </para>
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Size, long MtimeTicks, Guid Action), (bool? Ok, string Note)> VerdictCache = new();

    /// <summary>清空快取（供測試隔離；不影響任何判定）。</summary>
    public static void ClearVerdictCache() => VerdictCache.Clear();

    /// <summary>
    /// 這個檔案（任一動作）現在是否已有快取判定。以<b>該檔自己的指紋</b>查，
    /// 所以測試用唯一檔名就能與其他平行測試完全隔離。不影響驗證。
    /// </summary>
    public static bool IsVerdictCached(string filePath)
    {
        if (!TryFingerprint(filePath, out var full, out long size, out long mtime)) return false;
        return VerdictCache.ContainsKey((full, size, mtime, GenericVerifyV2))
            || VerdictCache.ContainsKey((full, size, mtime, DriverActionVerify));
    }

    /// <summary>取檔案指紋（完整路徑＋大小＋最後寫入時間）；檔案不存在或路徑不合法回 false。</summary>
    private static bool TryFingerprint(string filePath, out string full, out long size, out long mtime)
    {
        full = "";
        size = 0;
        mtime = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            full = Path.GetFullPath(filePath);
            var info = new FileInfo(full);
            if (!info.Exists) return false;
            size = info.Length;
            mtime = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>先查指紋快取；未命中才真的呼叫 wintrust，命中即回上次的逐字結果。</summary>
    private static (bool? Ok, string Note) VerifyCached(Guid action, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return (null, "檔案不存在——無法驗證");
        if (!TryFingerprint(filePath, out string full, out long size, out long mtime))
            return (null, "檔案不存在——無法驗證");

        if (Environment.GetEnvironmentVariable("XINSPECT_SIGCACHE") == "off")
            return VerifyAuthenticodeWith(action, full);   // 診斷閘：完全不經快取

        var key = (full, size, mtime, action);
        if (VerdictCache.TryGetValue(key, out var hit)) return hit;

        var verdict = VerifyAuthenticodeWith(action, full);
        if (VerdictCache.Count >= VerdictCacheMax) VerdictCache.Clear();
        VerdictCache[key] = verdict;
        return verdict;
    }

    private static (bool? Ok, string Note) VerifyAuthenticodeWith(Guid action, string filePath)
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
            Guid actionId = action;
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
    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2——一般檔案（非驅動）的驗證動作。</summary>
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

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
