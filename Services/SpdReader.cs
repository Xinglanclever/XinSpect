namespace XinSpect;

/// <summary>一個 SPD 位址上讀到什麼。</summary>
public enum SpdKind
{
    /// <summary>位址上沒有裝置——插槽是空的。正常狀況，不是錯誤。</summary>
    Empty,
    Ddr4,
    /// <summary>是 DDR5，但本讀取器不解 SPD5 hub 協定。</summary>
    Ddr5,
    /// <summary>型別碼認得出格式但不在支援範圍（DDR3 之類）。</summary>
    Unknown,
    /// <summary>有裝置卻讀不出可信的內容。<b>這是一筆發現，不是空插槽。</b></summary>
    Unreadable,
}

/// <param name="Raw">DDR4 的 512 位元組原始內容；只有 <see cref="SpdKind.Ddr4"/> 有值。</param>
/// <param name="Note">給人看的說明。乾淨讀到時是空字串——正常狀況不該產生雜訊。</param>
public sealed record SpdSlot(byte Address, SpdKind Kind, byte[]? Raw, string Note);

/// <summary>一次掃描的結果。</summary>
/// <param name="BusNote">
/// <b>匯流排層級</b>的結論，例如「這條匯流排上根本沒有 SPD」。這一格存在的理由是：
/// 那種情形下八個位址會各自回一句一模一樣的失敗訊息，而真正該講的是一句總結。
/// </param>
public sealed record SpdScan(IReadOnlyList<SpdSlot> Slots, string BusNote)
{
    /// <summary>有沒有任何位址上疑似掛著模組（含讀不到的——那也是一筆發現）。</summary>
    public bool AnyPresent => Slots.Any(s => s.Kind != SpdKind.Empty);
}

/// <summary>
/// 從 SMBus 上把每一條記憶體模組的 SPD 原始位元組取回來。
/// </summary>
/// <remarks>
/// <para>
/// 這一層只負責「把位元組原封不動地拿回來」，一個欄位都不解讀——解讀是
/// <c>SpdDecoder</c> 的事，而它是純函式、拿真實位元組基準檔驗證。這條分界是 1.9.1-B1
/// 那個 NVMe 位移錯誤的教訓：當「取值」與「解讀」混在一起，測試就只能照著實作的位移
/// 造合成資料，於是把錯誤鎖死了三個發佈版。
/// </para>
/// <para>
/// <b>全 0 與全 0xFF 一律判讀不到。</b>不轉送 SMBus 指令的 USB 外接盒與某些多工匯流排
/// 會帶 ACK 回一整片 0 或 F。把它當有效值解讀，會得出「製造於 2000 年第 0 週」這種
/// 看起來很像真的假結論——那正是驗機功能最不能犯的錯。
/// </para>
/// <para>
/// <b>讀完必須把頁復位回 0。</b>DDR4 超過 256 位元組要靠 SPA0／SPA1 切頁，而頁是
/// <i>匯流排上的狀態</i>：留在第 1 頁走人，下一個讀 SPD 的程式（CPU-Z、下次開機的 POST）
/// 在偏移 0 讀到的會是後半頁的內容。這是本檔唯一的副作用，所以走 finally 而不是快樂路徑。
/// </para>
/// </remarks>
public static class SpdReader
{
    /// <summary>SPD EEPROM 的八個裝置位址：0x50–0x57。</summary>
    public const byte FirstAddress = 0x50;
    public const int AddressCount = 8;

    /// <summary>SPD 位元組 2 的型別碼（DDR4＝0x0C、DDR5＝0x12，見 JEDEC SPD Annex）。</summary>
    public const byte TypeCodeOffset = 2;
    public const byte Ddr4TypeCode = 0x0C;
    public const byte Ddr5TypeCode = 0x12;

    public const int PageSize = 256;
    public const int Ddr4Size = 512;

    /// <summary>DDR4 的切頁裝置位址（SPA0／SPA1）。寫入這兩個位址是選頁，不是寫 EEPROM 資料區。</summary>
    public const byte PageSelect0 = 0x36;
    public const byte PageSelect1 = 0x37;

    /// <summary>掃過 0x50–0x57 全部八個位址。呼叫端必須先取得匯流排旗號。</summary>
    public static SpdScan ReadAll(SmbusController bus)
    {
        // 先探切頁裝置。DDR4 的 SPA0（0x36）只有在這條匯流排上真的掛著 DDR4 SPD 時才會回應，
        // 所以它 NAK 就代表「這條匯流排上沒有 SPD」——那是一句匯流排層級的結論，
        // 不該變成八個位址各自回一句一模一樣的「讀不到」。
        if (!bus.SendByte(PageSelect0, 0x00) && bus.LastStatus == SmbusStatus.NoDevice)
        {
            var empty = new List<SpdSlot>(AddressCount);
            for (int i = 0; i < AddressCount; i++)
                empty.Add(new SpdSlot((byte)(FirstAddress + i), SpdKind.Empty, null, ""));
            return new SpdScan(empty,
                "這條 SMBus 上沒有任何 DDR4 SPD——切頁裝置 0x36 沒有回應。"
                + "HEDT 與伺服器平台（X299、C621、LGA3647、Threadripper）的 DIMM SPD 通常掛在"
                + "處理器記憶體控制器自己的 SMBus 區段上，不在 PCH 這一條；本讀取器只實作 PCH 那條路徑。");
        }

        var slots = new List<SpdSlot>(AddressCount);
        for (int i = 0; i < AddressCount; i++) slots.Add(ReadOne(bus, (byte)(FirstAddress + i)));
        return new SpdScan(slots, "");
    }

    private static SpdSlot ReadOne(SmbusController bus, byte address)
    {
        if (!SelectPage(bus, 0))
            return new SpdSlot(address, SpdKind.Unreadable, null, "無法選擇 SPD 頁：" + bus.LastError);

        byte? type = bus.ReadByteData(address, TypeCodeOffset);
        if (type is null)
            return bus.LastStatus == SmbusStatus.NoDevice
                ? new SpdSlot(address, SpdKind.Empty, null, "")
                : new SpdSlot(address, SpdKind.Unreadable, null,
                    $"位址 0x{address:X2} 上像是有裝置，但讀不到 SPD 型別碼：{bus.LastError}");

        if (type is 0x00 or 0xFF)
            return new SpdSlot(address, SpdKind.Unreadable, null,
                $"位址 0x{address:X2} 的型別碼回了 0x{type:X2}（全 0／全 F），判為讀不到。"
                + "不轉送 SMBus 指令的外接盒與多工匯流排會帶 ACK 回一整片 0 或 F；"
                + "把它當有效值會解出「製造於 2000 年第 0 週」這種假結論。");

        if (type == Ddr5TypeCode)
            return new SpdSlot(address, SpdKind.Ddr5, null,
                "這條是 DDR5。SPD5 hub 的存取協定與 DDR4 不同，且本機沒有 DDR5 硬體可以拿真實"
                + "位元組驗證，因此不實作——沒有基準檔就寫解碼器，等於把猜測當成事實。");

        if (type != Ddr4TypeCode)
            return new SpdSlot(address, SpdKind.Unknown, null,
                $"未知的 SPD 型別碼 0x{type:X2}（本讀取器只解 DDR4），不解讀。");

        byte[]? raw = ReadDdr4(bus, address, out string note);
        return raw is null
            ? new SpdSlot(address, SpdKind.Unreadable, null, note)
            : new SpdSlot(address, SpdKind.Ddr4, raw, note);
    }

    /// <summary>把一條 DDR4 模組的 512 位元組全部讀回來（含切頁）。任何一個位元組讀不到就整條放棄。</summary>
    public static byte[]? ReadDdr4(SmbusController bus, byte address, out string note)
    {
        note = "";
        var raw = new byte[Ddr4Size];
        try
        {
            for (int page = 0; page < 2; page++)
            {
                if (!SelectPage(bus, page))
                {
                    note = $"無法切到 SPD 第 {page} 頁：{bus.LastError}";
                    return null;
                }
                for (int i = 0; i < PageSize; i++)
                {
                    byte? b = bus.ReadByteData(address, (byte)i);
                    if (b is null)
                    {
                        // 指出是哪一個位移，因為「哪裡開始讀不到」本身就是線索：
                        // 停在 0x100 通常是切頁沒生效，停在中途多半是模組或匯流排的問題。
                        note = $"位址 0x{address:X2} 的 SPD 在偏移 0x{page * PageSize + i:X2} 讀不到：{bus.LastError}";
                        return null;
                    }
                    raw[page * PageSize + i] = b.Value;
                }
            }
        }
        finally
        {
            SelectPage(bus, 0);
        }

        if (IsUniform(raw, out byte fill))
        {
            note = $"位址 0x{address:X2} 的 SPD 前 128 位元組全是 0x{fill:X2}，判為讀不到"
                 + "（真實的 SPD 不可能長這樣）。";
            return null;
        }
        return raw;
    }

    /// <summary>選頁：對 SPA0／SPA1 發一個位元組。這是寫入動作，但寫的是切頁裝置，不是 EEPROM 資料區。</summary>
    private static bool SelectPage(SmbusController bus, int page)
        => bus.SendByte(page == 0 ? PageSelect0 : PageSelect1, 0x00);

    private static bool IsUniform(byte[] raw, out byte fill)
    {
        fill = raw[0];
        if (fill is not (0x00 or 0xFF)) return false;
        for (int i = 1; i < 128; i++) if (raw[i] != fill) return false;
        return true;
    }
}
