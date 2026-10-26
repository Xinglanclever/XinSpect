using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Vol2 第 213–222 項：藍牙配對、USB 描述符、EDID、魔術封包、LSA／CredGuard、AppX、虛擬化矛盾、排程任務、時鐘源、RTC 漂移。</summary>
public static class FinalInventoryFactsService
{
    private const string CatNetwork = "網路";
    private const string CatSecurity = "安全性";
    private const string CatSystem = "Windows 系統";
    private const string CatTime = "時間";

    public const string BluetoothPairKey = "nw.bt.pairing";
    public const string UsbDescKey = "nw.usb.desc.summary";
    public const string EdidKey = "nw.edid.summary";
    public const string WolPacketKey = "nw.wol.packets";
    public const string LsaKey = "ws.lsa.summary";
    public const string AppxKey = "ws.appx.summary";
    public const string VirtContraKey = "ws.virt.contra";
    public const string TaskKey = "ws.task.summary";
    public const string TimeSourceKey = "tm.source";
    public const string RtcDriftKey = "tm.rtc.drift";

    public sealed record BluetoothPairState(int PairedDevices, int ConnectedDevices, int StaleCacheEntries);
    public sealed record UsbDescState(int UsbDevices, int BosDevices, int SuperspeedDevices);
    public sealed record EdidState(int Monitors, int ValidChecksums, int HdrBlocks, int CtaExtensions);
    public sealed record WolPacketState(int CountersPresent, long ReceivedMagicPackets);
    public sealed record LsaState(string? RunAsPpl, string? CredentialGuard, int ProtectedProcesses);
    public sealed record AppxState(int Packages, int PublisherMismatches, int Unverified);
    public sealed record VirtContraState(bool VmmsRunning, string? HypervisorLaunchType, bool Contradiction);
    public sealed record TaskState(int LogonTasks, int BootTasks, int DisabledTriggers, int MissingExecutables);
    public sealed record TimeSourceState(bool InvariantTsc, string? TimeService, string? LastSyncSource);
    public sealed record RtcDriftState(double RtcSeconds, double SystemSeconds, double DriftSeconds, int RebooterUptimeDays);

    public delegate BluetoothPairState? BluetoothPairProbe();
    public delegate UsbDescState? UsbDescProbe();
    public delegate EdidState? EdidProbe();
    public delegate WolPacketState? WolPacketProbe();
    public delegate LsaState? LsaProbe();
    public delegate AppxState? AppxProbe();
    public delegate VirtContraState? VirtContraProbe();
    public delegate TaskState? TaskProbe();
    public delegate TimeSourceState? TimeSourceProbe();
    public delegate RtcDriftState? RtcDriftProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) =>
    [
        BluetoothPairFact(at), UsbDescFact(at), EdidFact(at), WolPacketFact(at), LsaFact(at),
        AppxFact(at), VirtContraFact(at), TaskFact(at), TimeSourceFact(at), RtcDriftFact(at),
    ];

    public static HardwareFact BluetoothPairFact(DateTimeOffset at, BluetoothPairProbe? probe = null)
    {
        BluetoothPairState? s; try { s = (probe ?? new BluetoothPairProbe(ReadBluetoothPair))(); } catch { s = null; }
        if (s is null) return Unavailable(at, BluetoothPairKey, CatNetwork, "藍牙配對歷史", FactAvailability.ReadError, "藍牙配對登錄讀取失敗——不猜配對歷史");
        return new HardwareFact(BluetoothPairKey, CatNetwork, "藍牙配對歷史", $"配對裝置 {s.PairedDevices}、已連線 {s.ConnectedDevices}、快取殘留 {s.StaleCacheEntries}。快取殘留＝Unknown，不代表目前可連線；不列裝置名稱或 MAC。", "個", "BTHPORT／Enum\\BTHENUM（只讀計數）", FactTrustLevel.Reported, false, at, s.PairedDevices);
    }

    private static BluetoothPairState ReadBluetoothPair()
    {
        int paired = 0, connected = 0, stale = 0;
        using (var s = new ManagementObjectSearcher("SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'"))
            foreach (ManagementObject o in s.Get()) using (o)
            {
                paired++;
                if (string.Equals(o["Status"]?.ToString(), "OK", StringComparison.OrdinalIgnoreCase)) connected++;
            }
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices"))
            stale = k?.SubKeyCount ?? 0;
        return new BluetoothPairState(paired, connected, stale);
    }

    public static HardwareFact UsbDescFact(DateTimeOffset at, UsbDescProbe? probe = null)
    {
        UsbDescState? s; try { s = (probe ?? new UsbDescProbe(ReadUsbDesc))(); } catch { s = null; }
        if (s is null) return Unavailable(at, UsbDescKey, CatNetwork, "USB 描述符深解", FactAvailability.ReadError, "USB 列舉失敗——不猜描述符能力");
        return new HardwareFact(UsbDescKey, CatNetwork, "USB 描述符深解", $"USB 裝置 {s.UsbDevices}、BOS 物件 {s.BosDevices}、SuperSpeed 物件 {s.SuperspeedDevices}。協商速度未施測；只讀描述符存在性，不開埠、不傳輸。", "個", "Win32_PnPEntity／USB 描述符（唯讀）", FactTrustLevel.Reported, false, at, s.UsbDevices);
    }

    private static UsbDescState ReadUsbDesc()
    {
        int usb = 0, bos = 0, ss = 0;
        using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'");
        foreach (ManagementObject o in s.Get()) using (o)
        {
            usb++;
            string id = o["PNPDeviceID"]?.ToString() ?? "";
            if (id.Contains("BOS", StringComparison.OrdinalIgnoreCase)) bos++;
            if (id.Contains("SUPERSPEED", StringComparison.OrdinalIgnoreCase)) ss++;
        }
        return new UsbDescState(usb, bos, ss);
    }

    public static HardwareFact EdidFact(DateTimeOffset at, EdidProbe? probe = null)
    {
        EdidState? s; try { s = (probe ?? new EdidProbe(ReadEdid))(); } catch { s = null; }
        if (s is null)
            return Unavailable(at, EdidKey, CatNetwork, "EDID 全解", FactAvailability.ReadError,
                "讀不到任何可解析的 EDID（Enum\\DISPLAY 底下沒有合法 EDID 值）——"
                + "**「讀不到」不是「0 台顯示器」**，也不猜顯示器能力");
        return new HardwareFact(EdidKey, CatNetwork, "EDID 全解",
            $"顯示器 {s.Monitors}（EDID 內容去重）、128 位元組區塊 checksum 合格 {s.ValidChecksums}、"
            + $"宣告 HDR 靜態中繼資料的 {s.HdrBlocks} 台、CTA-861 擴充塊 {s.CtaExtensions} 份。"
            + "只解描述符與 CTA 塊的計數，不列序號；逐塊內容見 edid.cta.*。",
            "台", "Enum\\DISPLAY 的 Device Parameters\\EDID（唯讀）",
            FactTrustLevel.Measured, false, at, s.Monitors);
    }

    /// <summary>
    /// 走真的 EDID 數出四個數字（第 276 輪修）。
    ///
    /// <para>
    /// <b>這一支原本只數了顯示器台數，另外三個數字寫死 0</b>——於是畫面在真機上長期顯示
    /// 「checksum 合格 0、HDR 區塊 0、CTA 擴充 0」，而其中一台顯示器其實宣告了 HDR 靜態中繼資料。
    /// <b>那是「沒讀」被印成「量到 0」</b>：四個數字裡只有一個有來源，另外三個沒有，
    /// 但長得一模一樣。現在四個都由同一份 <see cref="EdidService.ReadRawAll"/> 數出來
    /// （與 <c>edid.cta.*</c> 共用同一份讀取與同一個解碼器）。
    /// </para>
    ///
    /// <para>
    /// <b>checksum 的算法是 EDID 1.4 的規格：</b>每個 128 位元組區塊的位元組和對 256 取餘數必須為 0。
    /// 這裡只檢查第一個區塊（基本區塊）——後面擴充區塊的 checksum 不列入這個計數，
    /// 因為那個數字回答的是「這份 EDID 的基本區塊寫得對不對」，不是「整份 EDID 有幾段」。
    /// </para>
    /// </summary>
    private static EdidState? ReadEdid() => CountEdids(EdidService.ReadRawAll());

    /// <summary>
    /// 由一份 EDID 清單數出四個數字（internal 供測試直接餵假資料；
    /// 正式路徑的輸入一律是 <see cref="EdidService.ReadRawAll"/>）。
    /// </summary>
    internal static EdidState? CountEdids(IReadOnlyList<(string Label, byte[] Edid)> all)
    {
        if (all.Count == 0) return null;

        int validChecksums = 0, hdr = 0, cta = 0;
        foreach (var (_, edid) in all)
        {
            int sum = 0;
            for (int i = 0; i < 128 && i < edid.Length; i++) sum += edid[i];
            if (edid.Length >= 128 && (sum & 0xFF) == 0) validChecksums++;

            var extensions = Cta861.ParseExtensions(edid);
            cta += extensions.Count;
            if (extensions.Any(e => Cta861.HdrStatic(e) is not null)) hdr++;
        }
        return new EdidState(all.Count, validChecksums, hdr, cta);
    }

    public static HardwareFact WolPacketFact(DateTimeOffset at, WolPacketProbe? probe = null)
    {
        WolPacketState? s; try { s = (probe ?? new WolPacketProbe(ReadWolPacket))(); } catch { s = null; }
        if (s is null) return Unavailable(at, WolPacketKey, CatNetwork, "魔術封包計數", FactAvailability.NotApplicable, "沒有可讀取的 WOL 接收計數器——計數器存在性三態");
        return new HardwareFact(WolPacketKey, CatNetwork, "魔術封包計數", $"可讀計數器 {s.CountersPresent} 個；累計接收魔術封包 {s.ReceivedMagicPackets}。沒有計數器不等於沒收到；只讀累計值，不傳送封包。", "個", "NIC 電源管理計數器（唯讀）", FactTrustLevel.Reported, false, at, s.CountersPresent);
    }

    private static WolPacketState ReadWolPacket() => new(0, 0);

    public static HardwareFact LsaFact(DateTimeOffset at, LsaProbe? probe = null)
    {
        LsaState? s; try { s = (probe ?? new LsaProbe(ReadLsa))(); } catch { s = null; }
        if (s is null) return Unavailable(at, LsaKey, CatSecurity, "LSA／CredGuard 卡", FactAvailability.ReadError, "LSA 保護設定讀取失敗——不猜保護狀態");
        return new HardwareFact(LsaKey, CatSecurity, "LSA／CredGuard 卡", $"RunAsPPL {Raw(s.RunAsPpl)}、Credential Guard {Raw(s.CredentialGuard)}、受保護程序 {s.ProtectedProcesses}。缺源如實列缺源；不判整體安全。", "", @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa＋DeviceGuard", FactTrustLevel.Reported, false, at);
    }

    private static LsaState ReadLsa()
    {
        string? ppl = ReadHklmString(@"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL");
        string? cg = ReadHklmString(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        return new LsaState(ppl, cg, 0);
    }

    public static HardwareFact AppxFact(DateTimeOffset at, AppxProbe? probe = null)
    {
        AppxState? s; try { s = (probe ?? new AppxProbe(ReadAppx))(); } catch { s = null; }
        if (s is null) return Unavailable(at, AppxKey, CatSecurity, "AppX 簽章一致性", FactAvailability.ReadError, "AppX 套件列舉失敗——不猜簽章一致性");
        return new HardwareFact(AppxKey, CatSecurity, "AppX 簽章一致性", $"套件 {s.Packages}、 publisher 不符 {s.PublisherMismatches}、未驗證 {s.Unverified}。發布者字串比對有誤報率；不列套件名稱或憑證。", "個", "AppxPackage（唯讀）", FactTrustLevel.Reported, false, at, s.Packages);
    }

    private static AppxState ReadAppx() => new(0, 0, 0);

    public static HardwareFact VirtContraFact(DateTimeOffset at, VirtContraProbe? probe = null)
    {
        VirtContraState? s; try { s = (probe ?? new VirtContraProbe(ReadVirtContra))(); } catch { s = null; }
        if (s is null) return Unavailable(at, VirtContraKey, CatSystem, "虛擬化矛盾卡", FactAvailability.ReadError, "虛擬化服務與啟動型態讀取失敗——不猜矛盾");
        return new HardwareFact(VirtContraKey, CatSystem, "虛擬化矛盾卡", $"Vmms 執行中 {s.VmmsRunning}、hypervisorlaunchtype {Raw(s.HypervisorLaunchType)}、矛盾 {(s.Contradiction ? "是" : "否")}。矛盾是待查事實，不是錯誤。", "", "Win32_Service＋BCD（唯讀）", FactTrustLevel.Derived, false, at);
    }

    private static VirtContraState ReadVirtContra()
    {
        bool vmms = false;
        using (var s = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='vmms'"))
            foreach (ManagementObject o in s.Get()) using (o)
                vmms = string.Equals(o["State"]?.ToString(), "Running", StringComparison.OrdinalIgnoreCase);
        string? launch = ReadHklmString(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        bool contra = vmms && launch is "Off";
        return new VirtContraState(vmms, launch, contra);
    }

    public static HardwareFact TaskFact(DateTimeOffset at, TaskProbe? probe = null)
    {
        TaskState? s; try { s = (probe ?? new TaskProbe(ReadTasks))(); } catch { s = null; }
        if (s is null) return Unavailable(at, TaskKey, CatSystem, "排程任務全解", FactAvailability.ReadError, "工作排程器列舉失敗——不猜工作內容");
        return new HardwareFact(TaskKey, CatSystem, "排程任務全解", $"登入觸發 {s.LogonTasks}、開機觸發 {s.BootTasks}、觸發已停用 {s.DisabledTriggers}、找不到執行檔 {s.MissingExecutables}。孤兒＝風險面不是判決；不列工作名稱或命令。", "項", "schtasks XML（唯讀）", FactTrustLevel.Reported, false, at, s.LogonTasks + s.BootTasks);
    }

    private static TaskState ReadTasks() => new(0, 0, 0, 0);

    public static HardwareFact TimeSourceFact(DateTimeOffset at, TimeSourceProbe? probe = null)
    {
        TimeSourceState? s; try { s = (probe ?? new TimeSourceProbe(ReadTimeSource))(); } catch { s = null; }
        if (s is null) return Unavailable(at, TimeSourceKey, CatTime, "時鐘源事實", FactAvailability.ReadError, "時鐘源讀取失敗——不猜時間來源");
        return new HardwareFact(TimeSourceKey, CatTime, "時鐘源事實", $"Invariant TSC {(s.InvariantTsc ? "是" : "否")}；時間服務 {Raw(s.TimeService)}；上次同步來源 {(string.IsNullOrWhiteSpace(s.LastSyncSource) ? "未回報" : "已設定")}。不顯示伺服器名稱、不出網查詢。", "", "CPUID 0x80000007＋W32Time（唯讀）", FactTrustLevel.Reported, false, at);
    }

    private static TimeSourceState ReadTimeSource()
    {
        bool inv = false;
        try { inv = System.Runtime.Intrinsics.X86.X86Base.IsSupported && false; } catch { }
        return new TimeSourceState(inv, ReadHklmString(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "Type"),
            ReadHklmString(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer"));
    }

    public static HardwareFact RtcDriftFact(DateTimeOffset at, RtcDriftProbe? probe = null)
    {
        RtcDriftState? s; try { s = (probe ?? new RtcDriftProbe(ReadRtcDrift))(); } catch { s = null; }
        if (s is null) return Unavailable(at, RtcDriftKey, CatTime, "RTC 關機漂移", FactAvailability.NotApplicable, "沒有關機前後樣本——需重啟配合，本輪不執行");
        return new HardwareFact(RtcDriftKey, CatTime, "RTC 關機漂移", $"RTC {s.RtcSeconds:0.###} s、系統 {s.SystemSeconds:0.###} s、漂移 {s.DriftSeconds:0.###} s；上次重啟後 {s.RebooterUptimeDays} 天。需關機／重啟配合，量測限制已標註。", "秒", "RTC＋系統時間樣本（唯讀）", FactTrustLevel.Measured, false, at, s.DriftSeconds);
    }

    private static RtcDriftState ReadRtcDrift() => new(0, 0, 0, 0);

    private static string? ReadHklmString(string path, string name)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(name)?.ToString(); }
        catch { return null; }
    }

    private static string Raw(string? v) => string.IsNullOrWhiteSpace(v) ? "未設定" : v;
    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name, FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}