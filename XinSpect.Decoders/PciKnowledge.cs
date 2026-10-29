namespace XinSpect;

/// <summary>
/// PCI 知識層的純解碼器（V7 WP30／D2 第一批）：PCI-SIG 規格定義的穩定知識——
/// 類別碼（base/subclass/progIF）與廠商 ID。<b>只收錄規格或 PCI-SIG 登錄檔有據的條目</b>；
/// 未收錄的如實回原始碼——知識庫的價值在「說得出出處的名字」，猜測的名字比沒有更糟。
/// device 型號對照（每代更新、文件分散）刻意不納入本表，待 WP30 後續批次有出處化資料再補。
/// </summary>
public static class PciKnowledge
{
    /// <summary>解設定空間 0x00 的 vendor/device ID dword。</summary>
    [SpecRef("PCI Local Bus Spec 3.0 §6.2.1：Vendor ID u16@0x00、Device ID u16@0x02")]
    public static (ushort VendorId, ushort DeviceId) DecodeVendorDevice(uint raw)
        => ((ushort)(raw & 0xFFFF), (ushort)(raw >> 16));

    /// <summary>解設定空間 0x08 的類別碼 dword：base bits[31:24]、subclass bits[23:16]、progIF bits[15:8]、revision bits[7:0]。</summary>
    [SpecRef("PCI-SIG, PCI Code and Vendor ID Assignments Spec（Rev 1.x），Class Code 暫存器佈局見 PCI Local Bus Spec 3.0 §6.2.1（0x08：base[31:24]/sub[23:16]/progIF[15:8]/rev[7:0]）")]
    public static (byte BaseClass, byte SubClass, byte ProgIf, byte Revision) DecodeClassCode(uint raw)
        => ((byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw);

    /// <summary>Base class → 角色名（PCI Code and ID Assignments Spec）。未收錄回 null。</summary>
    [SpecRef("PCI-SIG, PCI Code and Vendor ID Assignments Spec 的 Base Class 定義表")]
    public static string? BaseClassName(byte baseClass) => baseClass switch
    {
        0x00 => "未分類（類別碼問世前的舊裝置）",
        0x01 => "大量儲存控制器",
        0x02 => "網路控制器",
        0x03 => "顯示控制器",
        0x04 => "多媒體裝置",
        0x05 => "記憶體控制器",
        0x06 => "橋接裝置",
        0x07 => "簡單通訊控制器",
        0x08 => "通用系統周邊",
        0x09 => "輸入裝置",
        0x0A => "擴充基座（Docking Station）",
        0x0B => "處理器",
        0x0C => "序列匯流排控制器",
        0x0D => "無線控制器",
        0x0E => "智慧型 I/O 控制器",
        0x0F => "衛星通訊控制器",
        0x10 => "加密控制器",
        0x11 => "訊號處理控制器",
        0xFF => "未分類（裝置未宣告類別）",
        _ => null,
    };

    /// <summary>常用 subclass → 細節名（僅收錄 PCI-SIG 規格有據且常見者）。未收錄回 null——只報 base class＋原始碼。</summary>
    [SpecRef("PCI-SIG, PCI Code and Vendor ID Assignments Spec 的 Sub-Class 定義表（僅收錄常見子集）")]
    public static string? SubClassName(byte baseClass, byte subClass) => (baseClass, subClass) switch
    {
        (0x01, 0x06) => "SATA",
        (0x01, 0x08) => "NVMe（非揮發性記憶體）",
        (0x01, 0x04) => "RAID",
        (0x02, 0x00) => "Ethernet",
        (0x03, 0x00) => "VGA 相容顯示",
        (0x04, 0x01) => "多媒體音訊",
        (0x04, 0x03) => "HD Audio（HDA 控制器）",
        (0x05, 0x00) => "RAM（主記憶體控制器）",
        (0x06, 0x00) => "Host Bridge（主機橋）",
        (0x06, 0x01) => "ISA Bridge（LPC/eSPI）",
        (0x06, 0x04) => "PCI-to-PCI Bridge",
        (0x07, 0x00) => "串列通訊",
        (0x08, 0x00) => "中斷控制器（PIC）",
        (0x08, 0x02) => "系統計時器",
        (0x08, 0x03) => "RTC（即時時鐘）",
        (0x0C, 0x00) => "FireWire（IEEE 1394）",
        (0x0C, 0x03) => "USB",
        (0x0C, 0x05) => "SMBus",
        (0x0C, 0x80) => "其他序列匯流排",
        _ => null,
    };

    /// <summary>PCI-SIG 廠商 ID 登錄的知名子集。未收錄回 null——報「Vendor 0xXXXX（未收錄）」而非猜名。</summary>
    [SpecRef("PCI-SIG, PCI Code and Vendor ID Assignments Spec 的 Vendor ID 登錄（僅收錄知名子集）")]
    public static string? VendorName(ushort vendorId) => vendorId switch
    {
        0x8086 => "Intel",
        0x1022 => "AMD",
        0x1002 => "AMD（ATI）",
        0x10DE => "NVIDIA",
        0x10EC => "Realtek",
        0x14C3 => "MediaTek",
        0x168C => "Qualcomm Atheros",
        0x1969 => "Qualcomm Atheros（Attansic）",
        0x1B4B => "Marvell",
        0x15AD => "VMware",
        0x1AF4 => "Red Hat（virtio）",
        0x1B36 => "Red Hat（QEMU）",
        0x80EE => "VirtualBox",
        0x1414 => "Microsoft",
        _ => null,
    };

    /// <summary>一行裝置描述：角色（類別碼知識）＋廠商＋原始 ID。全部段落可從原始碼稽核。</summary>
    [SpecRef("本專案呈現方法學：規格有據的名字與原始碼並列（V7 §12.2 能力與實際並列）；未收錄項目如實標示")]
    public static string Describe((ushort VendorId, ushort DeviceId) ids, (byte BaseClass, byte SubClass, byte ProgIf, byte Revision) cls)
    {
        string role = BaseClassName(cls.BaseClass) is { } baseName
            ? SubClassName(cls.BaseClass, cls.SubClass) is { } subName ? $"{baseName}／{subName}" : baseName
            : $"類別碼未收錄";
        string vendor = VendorName(ids.VendorId) ?? $"Vendor 0x{ids.VendorId:X4}（未收錄）";
        return $"{role} ・ {vendor}（0x{ids.VendorId:X4}:0x{ids.DeviceId:X4}） ・ 類別碼 0x{cls.BaseClass:X2}{cls.SubClass:X2}/progIF 0x{cls.ProgIf:X2} rev 0x{cls.Revision:X2}";
    }

    /// <summary>
    /// progIF（程式介面）→ 細節名：<b>只收錄 PCI-SIG 指派表裡沒有爭議的幾格</b>。
    /// 未收錄回 null——呼叫端要如實寫「本版沒有這一格的名稱，只報原值」，
    /// 不能拿子類別的名字冒充（USB 控制器與 USB 主機是兩件事）。
    /// </summary>
    [SpecRef("PCI-SIG, PCI Code and Vendor ID Assignments Spec 的 Programming Interface 指派（僅收錄 USB 控制器、NVMe、AHCI 三組常見格）")]
    public static string? ProgIfName(byte baseClass, byte subClass, byte progIf) => (baseClass, subClass) switch
    {
        (0x0C, 0x03) => progIf switch
        {
            0x00 => "UHCI（USB 1.x 主機控制器）",
            0x10 => "OHCI（USB 1.x 主機控制器）",
            0x20 => "EHCI（USB 2.0 主機控制器）",
            0x30 => "XHCI（USB 3.x 主機控制器）",
            _ => null,
        },
        (0x01, 0x08) => progIf switch
        {
            0x01 => "NVMHCI",
            0x02 => "NVMe（NVM Express）",
            _ => null,
        },
        (0x01, 0x06) => progIf switch
        {
            0x01 => "AHCI 1.0",
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// 本表三段的條數（基底類別／子類別／廠商）。<b>由程式數出來，不手抄</b>——
    /// 內建清冊與覆蓋申報都讀這裡，表改了申報跟著動。
    /// </summary>
    [SpecRef("計數即上列三張對照表的成員數（基底類別／子類別／廠商）——逐值掃描列舉域，不由手抄而來的常數")]
    public static (int BaseClasses, int SubClasses, int Vendors) Counts() => LazyCounts.Value;

    /// <summary>progIF 表收錄幾格（由程式掃描列舉域數出來，不手抄）。</summary>
    [SpecRef("計數即 ProgIfName 對照表的成員數——逐值掃描列舉域，不由手抄的常數（與 Counts 同一個規矩）")]
    public static int ProgIfCount()
    {
        int n = 0;
        for (int b = 0; b <= 0xFF; b++)
            for (int s = 0; s <= 0xFF; s++)
            {
                // 先問子類別表：progIF 只在有子類別的 (base, sub) 底下才有意義，這樣是 25×256 而不是 256³。
                if (SubClassName((byte)b, (byte)s) is null) continue;
                for (int p = 0; p <= 0xFF; p++)
                    if (ProgIfName((byte)b, (byte)s, (byte)p) is not null) n++;
            }
        return n;
    }

    private static readonly System.Lazy<(int, int, int)> LazyCounts = new(() =>
    {
        int bases = 0, subs = 0, vendors = 0;
        for (int b = 0; b <= 0xFF; b++)
            if (BaseClassName((byte)b) is not null) bases++;
        for (int b = 0; b <= 0xFF; b++)
            for (int s = 0; s <= 0xFF; s++)
                if (SubClassName((byte)b, (byte)s) is not null) subs++;
        for (int v = 0; v <= 0xFFFF; v++)
            if (VendorName((ushort)v) is not null) vendors++;
        return (bases, subs, vendors);
    }, isThreadSafe: true);
}
