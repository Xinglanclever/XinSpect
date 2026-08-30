using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// BIOS／Intel ME 韌體與微碼：唯讀直讀 + 官方刷寫路徑導向。
/// </summary>
/// <remarks>
/// <para>誠實與安全界線（刻意設計，不是未完成）：</para>
/// <list type="bullet">
/// <item>本服務<b>不寫入任何韌體</b>。使用者模式要寫 BIOS／ME 區域必須自帶核心驅動並繞過 Flash 寫入保護，
/// 寫壞的結果是主機板無法開機，且無法用軟體救回。</item>
/// <item>ME 韌體版本以 HECI／MEI 介面發 MKHI <c>GET_FW_VERSION</c> 詢問 ME 本身；
/// 讀不到就說讀不到，絕不用驅動版本或 PCH 型號去猜韌體版本。</item>
/// <item>微碼同時列出「目前生效」與「Windows 自帶偏好」兩個版本，並說明誰覆蓋誰——
/// 只報一個數字會讓人誤以為 BIOS 微碼就是實際跑的那份。</item>
/// </list>
/// </remarks>
public sealed class BiosMeService : ObservableObject
{
    private bool _loading;
    public bool IsLoading
    {
        get => _loading;
        private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<FirmwareRow> Bios { get; } = [];
    public ObservableCollection<FirmwareRow> Me { get; } = [];
    public ObservableCollection<FirmwareRow> Microcode { get; } = [];

    /// <summary>危險區的固定警告文字（繫結用）。</summary>
    public string DangerNotice => BiosMeDecoder.DangerNotice;

    private bool _riskAccepted;
    /// <summary>使用者是否已明示理解風險。未勾選前，危險區的按鈕全部停用。</summary>
    public bool RiskAccepted
    {
        get => _riskAccepted;
        set { if (SetProperty(ref _riskAccepted, value)) OnPropertyChanged(nameof(CanActDangerously)); }
    }
    public bool CanActDangerously => _riskAccepted;

    private string _actionStatus = "";
    public string ActionStatus { get => _actionStatus; private set => SetProperty(ref _actionStatus, value); }

    private string? _vendorUrl;
    /// <summary>本機主機板廠商的官方下載頁；比對不到廠商時為 null（按鈕會顯示為不可用）。</summary>
    public string? VendorUrl { get => _vendorUrl; private set { if (SetProperty(ref _vendorUrl, value)) OnPropertyChanged(nameof(HasVendorUrl)); } }
    public bool HasVendorUrl => _vendorUrl is not null;

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        Status = "讀取中…";
        try
        {
            var (bios, me, mc, vendor, note) = await Task.Run(ReadAll);
            Bios.Clear(); foreach (var r in bios) Bios.Add(r);
            Me.Clear(); foreach (var r in me) Me.Add(r);
            Microcode.Clear(); foreach (var r in mc) Microcode.Add(r);
            VendorUrl = BiosMeDecoder.VendorBiosUrl(vendor);
            Status = note;
        }
        catch (Exception ex)
        {
            Status = "讀取失敗：" + ex.Message;
        }
        finally { IsLoading = false; }
    }

    private (List<FirmwareRow> Bios, List<FirmwareRow> Me, List<FirmwareRow> Mc, string? Vendor, string Note) ReadAll()
    {
        var bios = new List<FirmwareRow>();
        string? vendor = null;

        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_BIOS");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    vendor = o["Manufacturer"] as string;
                    bios.Add(new FirmwareRow("BIOS 廠商", vendor ?? "—"));
                    bios.Add(new FirmwareRow("BIOS 版本", o["SMBIOSBIOSVersion"] as string ?? "—"));
                    bios.Add(new FirmwareRow("發行日期", FormatCimDate(o["ReleaseDate"] as string)));
                    bios.Add(new FirmwareRow("SMBIOS 版本",
                        $"{o["SMBIOSMajorVersion"]}.{o["SMBIOSMinorVersion"]}"));
                    if (o["BIOSVersion"] is string[] bv && bv.Length > 0)
                        bios.Add(new FirmwareRow("韌體版本字串", string.Join(" ／ ", bv)));
                }
                break;   // 一台機器只有一份 BIOS
            }
        }
        catch (Exception ex) { bios.Add(new FirmwareRow("BIOS", "—（WMI 讀取失敗：" + ex.Message + "）")); }

        // 開機模式：UEFI 才會有 SecureBoot\State 這個鍵（Legacy／CSM 開機時整個鍵不存在）
        using (var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            bios.Add(new FirmwareRow("開機模式", sb is null
                ? "Legacy／CSM（登錄檔無 SecureBoot\\State 鍵）"
                : "UEFI（Secure Boot 的啟用與否見下方「韌體與開機信任鏈」卡片）"));
        }

        var me = ReadMe();
        var mc = ReadMicrocode();
        string note = "全部唯讀完成。本卡片不寫入任何韌體——BIOS／ME 的刷寫一律交給主機板廠的官方工具。";
        return (bios, me, mc, vendor, note);
    }

    private static string FormatCimDate(string? cim)
    {
        if (cim is null || cim.Length < 8) return "—";
        return DateTime.TryParseExact(cim[..8], "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd") : "—";
    }

    /// <summary>
    /// 微碼：MSR 0x8B 的本核讀值，加上登錄檔中 Windows 記下的「目前／Windows 偏好」兩個版本。
    /// </summary>
    private static List<FirmwareRow> ReadMicrocode()
    {
        var rows = new List<FirmwareRow>();
        uint? current = null, preferred = null, firmwareRec = null, status = null;

        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            if (k is null) rows.Add(new FirmwareRow("登錄檔", "—（CentralProcessor\\0 鍵讀不到）"));
            else
            {
                current = BiosMeDecoder.DecodeUpdateRevision(k.GetValue("Update Revision") as byte[]);
                preferred = k.GetValue("Preferred Record Version") as int? is int p ? unchecked((uint)p) : null;
                firmwareRec = k.GetValue("Firmware Record Version") as int? is int f ? unchecked((uint)f) : null;
                status = k.GetValue("Update Status") as int? is int u ? unchecked((uint)u) : null;
            }
        }

        rows.Add(new FirmwareRow("目前生效（Update Revision）",
            current is null ? "—（登錄檔無此值）" : $"0x{current:X8}"));
        rows.Add(new FirmwareRow("韌體內建（Firmware Record Version）",
            firmwareRec is null ? "—" : $"0x{firmwareRec:X8}（BIOS 映像裡附的那份）"));
        rows.Add(new FirmwareRow("Windows 自帶偏好（Preferred）",
            preferred is null ? "—" : $"0x{preferred:X8}（mcupdate_GenuineIntel.dll 內的版本）"));
        rows.Add(new FirmwareRow("誰覆蓋誰", BiosMeDecoder.CompareMicrocode(current, preferred)));
        rows.Add(new FirmwareRow("更新狀態（Update Status）", BiosMeDecoder.DescribeUpdateStatus(status)));

        // MSR 0x8B 直讀：與登錄檔互為佐證。讀不到就說讀不到，不用登錄檔的值假冒 MSR 讀值。
        try
        {
            using var bridge = WinRing0Bridge.Create();
            if (!bridge.Available)
                rows.Add(new FirmwareRow("MSR 0x8B 直讀", "—（MSR 橋接無法初始化：" + bridge.Error + "）"));
            else
            {
                System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);   // 讀 0x8B 前須先執行 CPUID
                ulong? sign = bridge.ReadMsrPair64(0x8B);
                rows.Add(new FirmwareRow("MSR 0x8B 直讀",
                    sign is null ? "—（讀取失敗）"
                    : (sign.Value >> 32) == 0 ? "—（高 32 位為 0：本平台未回報微碼版本）"
                    : $"0x{sign.Value >> 32:X8}（本核當下值）"));
            }
        }
        catch (Exception ex) { rows.Add(new FirmwareRow("MSR 0x8B 直讀", "—（" + ex.Message + "）")); }

        string mcupdate = Path.Combine(Environment.SystemDirectory, "mcupdate_GenuineIntel.dll");
        if (!File.Exists(mcupdate))
            rows.Add(new FirmwareRow("Windows 微碼載入器",
                "—（本機無 mcupdate_GenuineIntel.dll：Windows 不會在開機時覆蓋微碼）"));
        else
        {
            // 本機實測這個 DLL 沒有填 FileVersion 資源（微碼資料檔常如此），空字串就照實說空，不假裝有版本
            string? fv = FileVersionInfo.GetVersionInfo(mcupdate).FileVersion;
            rows.Add(new FirmwareRow("Windows 微碼載入器",
                string.IsNullOrWhiteSpace(fv)
                    ? $"存在：{mcupdate}（檔案本身未填版本資源；微碼版本見上方 Preferred 一列）"
                    : $"存在：{mcupdate}（{fv}）"));
        }
        return rows;
    }

    // ── Intel ME：HECI／MEI 介面直接問 ME 韌體版本（唯讀，MKHI GET_FW_VERSION）────────

    private static readonly Guid MeiInterfaceGuid = new("e2d1ff34-3458-49a9-88da-8e6915ce9be5");
    private static readonly Guid MkhiClientGuid = new("8e6a6715-9abc-4043-88ef-9e39c6f63e0f");
    private const uint IoctlTeeConnectClient = 0x8000E004;
    private const uint MkhiGetFwVersionRequest = 0xFF | (0x02 << 8);   // GroupId 0xFF、Command 0x02

    private static List<FirmwareRow> ReadMe()
    {
        var rows = new List<FirmwareRow>();

        // 先確認 HECI 裝置與驅動：ME 韌體版本讀不到時，這兩列能區分「沒這顆晶片」與「有但問不到」
        string? device = null, driver = null;
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%Management Engine%'");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    device = o["DeviceName"] as string;
                    driver = o["DriverVersion"] as string;
                }
                break;
            }
        }
        catch { /* WMI 讀不到就留 null，下面照實說 */ }

        rows.Add(new FirmwareRow("HECI／MEI 裝置", device ?? "—（找不到 Intel Management Engine Interface 裝置）"));
        rows.Add(new FirmwareRow("驅動版本", driver ?? "—"));
        rows.Add(new FirmwareRow("⚠ 別把兩者混為一談",
            "上面那個是 Windows 驅動的版本，不是 ME 韌體版本。ME 韌體版本只能問 ME 自己（下一列）。"));

        var (version, note) = QueryMeFirmwareVersion();
        rows.Add(new FirmwareRow("ME 韌體版本（MKHI 直詢）", version));
        if (version.StartsWith('—')) rows.Add(new FirmwareRow("讀不到的原因", note));
        else rows.Add(new FirmwareRow("世代", BiosMeDecoder.DescribeMeGeneration(FirstVersionToken(version))));
        return rows;
    }

    /// <summary>「作業碼（Operational） 11.8.50.3425 ・ …」→「11.8.50.3425」。</summary>
    private static string FirstVersionToken(string s)
        => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.Contains('.')) ?? s;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SpDeviceInterfaceData data,
        nint detail, uint detailSize, out uint required, nint devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    private const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;

    /// <summary>列出 MEI 裝置介面路徑（\\?\pci#...）。找不到就回 null。</summary>
    private static string? FindMeiDevicePath()
    {
        var guid = MeiInterfaceGuid;
        nint set = SetupDiGetClassDevs(ref guid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1 || set == 0) return null;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, 0, ref data)) return null;

            SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out uint needed, 0);
            if (needed == 0) return null;
            nint buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W 的 cbSize 是「結構本體」大小而非緩衝區大小：
                // 64 位元下為 8，32 位元下為 6。填成 needed 會被回 ERROR_INVALID_USER_BUFFER。
                Marshal.WriteInt32(buf, 0, nint.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, needed, out _, 0)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string path, uint access, uint share, nint sec,
                                          uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint h, uint code, byte[]? inBuf, uint inSize,
                                              byte[]? outBuf, uint outSize, out uint returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(nint h, byte[] buf, uint toWrite, out uint written, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(nint h, byte[] buf, uint toRead, out uint read, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint h);

    /// <summary>
    /// 以 MEI 介面向 ME 發 MKHI <c>GET_FW_VERSION</c>（唯讀查詢，不改任何 ME 設定）。
    /// 回傳 (版本字串, 讀不到時的原因)。每一步失敗都各有自己的原因文字——
    /// 「讀不到」和「沒這顆晶片」是兩件事，混在一起講就是騙人。
    /// </summary>
    private static (string Version, string Note) QueryMeFirmwareVersion()
    {
        string? path = FindMeiDevicePath();
        if (path is null)
            return ("—（找不到 MEI 裝置介面）", "本機沒有啟用的 Intel ME／CSME 介面，或 MEI 驅動未載入（AMD 平台正常如此）。");

        nint h = CreateFile(path, 0x80000000 | 0x40000000, 0x00000003, 0, 3, 0, 0);   // GENERIC_READ|WRITE、共用讀寫
        if (h == -1)
            return ("—（無法開啟 MEI 裝置）", $"CreateFile 失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}（此介面通常需要系統管理員權限）。");

        try
        {
            byte[] client = MkhiClientGuid.ToByteArray();
            var props = new byte[8];   // MaxMessageLength(4) + ProtocolVersion(1) + 保留
            if (!DeviceIoControl(h, IoctlTeeConnectClient, client, (uint)client.Length, props, (uint)props.Length, out _, 0))
                return ("—（無法連線到 ME 的 MKHI 用戶端）",
                        $"IOCTL_TEE_CONNECT_CLIENT 失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}（ME 可能處於製造模式或已被停用）。");

            byte[] req = BitConverter.GetBytes(MkhiGetFwVersionRequest);
            if (!WriteFile(h, req, (uint)req.Length, out _, 0))
                return ("—（要求送不出去）", $"WriteFile 失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}。");

            // 讀取緩衝區必須至少等於用戶端宣告的 MaxMessageLength：MEI 驅動不做部分回傳，
            // 給小了會直接回 ERROR_INSUFFICIENT_BUFFER（122）並回 0 位元組——本機實測即為此。
            uint maxMsg = BitConverter.ToUInt32(props, 0);
            var resp = new byte[Math.Clamp(maxMsg, 64u, 65536u)];
            if (!ReadFile(h, resp, (uint)resp.Length, out uint got, 0) || got < 12)
                return ("—（ME 沒有回覆完整版本）",
                        $"ReadFile 回 {got} 位元組（至少需 12），緩衝區 {resp.Length}（用戶端宣告 {maxMsg}），"
                        + $"Win32 錯誤 {Marshal.GetLastWin32Error()}。");

            // 前 4 位元組是 MKHI 標頭（含 Result），其後才是各分割區的版本
            uint header = BitConverter.ToUInt32(resp, 0);
            uint result = (header >> 24) & 0xFF;
            if (result != 0)
                return ($"—（ME 回報錯誤碼 0x{result:X2}）", "MKHI 標頭的 Result 欄非 0，代表 ME 拒絕了這個查詢。");

            var payload = resp.Skip(4).Take((int)got - 4).ToArray();
            return (BiosMeDecoder.DecodeMeFwVersion(payload), "");
        }
        catch (Exception ex) { return ("—（查詢過程發生例外）", ex.Message); }
        finally { CloseHandle(h); }
    }

    // ── 危險區：本程式唯一允許的兩個「會改變狀態」的動作，且都不寫韌體 ────────────────

    /// <summary>
    /// 重開機並直接進入主機板的 UEFI 設定畫面（<c>shutdown /r /fw /t 0</c>）。
    /// </summary>
    /// <remarks>
    /// 這是本卡片唯一會真的動到機器的動作，而且它動的是「重開機」而不是韌體內容：
    /// BIOS 設定本身仍由主機板自己的介面負責修改。未勾選風險確認前不執行；
    /// 未存檔的工作會隨重開機遺失，故呼叫端必須再做一次確認對話框。
    /// </remarks>
    public bool RebootToFirmwareSetup()
    {
        if (!_riskAccepted) { ActionStatus = "請先勾選風險確認。"; return false; }
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 0")
            {
                UseShellExecute = true,
                Verb = "runas",          // /fw 需要系統管理員權限
                CreateNoWindow = true,
            });
            ActionStatus = "已要求重開機進入 UEFI 設定；若沒有反應，代表權限提升被取消或本機為 Legacy 開機。";
            return true;
        }
        catch (Exception ex)
        {
            ActionStatus = "無法要求重開機：" + ex.Message;
            return false;
        }
    }

    /// <summary>開啟本機主機板廠商的官方支援／BIOS 下載頁。比對不到廠商時什麼都不做。</summary>
    public void OpenVendorBiosPage()
    {
        if (_vendorUrl is null) { ActionStatus = "認不出主機板廠商，不亂連——請自行到板廠官網查你的板子型號。"; return; }
        try
        {
            Process.Start(new ProcessStartInfo(_vendorUrl) { UseShellExecute = true });
            ActionStatus = "已開啟：" + _vendorUrl;
        }
        catch (Exception ex) { ActionStatus = "開啟失敗：" + ex.Message; }
    }

    /// <summary>開啟 Intel 官方的 CSME 版本偵測工具頁（唯讀工具，非刷寫工具）。</summary>
    public void OpenIntelMeToolPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://www.intel.com.tw/content/www/tw/zh/support/articles/000029389/software/chipset-software.html")
            { UseShellExecute = true });
            ActionStatus = "已開啟 Intel CSME 版本偵測工具說明頁。";
        }
        catch (Exception ex) { ActionStatus = "開啟失敗：" + ex.Message; }
    }
}
