namespace XinSpect;

/// <summary>SMART／健康紀錄的關鍵警告位元之一。</summary>
public readonly record struct NvmeCriticalWarning(int Bit, string Name, string Meaning);

/// <summary>錯誤資訊紀錄（log page 0x01）的一筆。</summary>
public sealed class NvmeErrorEntry
{
    public required ulong ErrorCount { get; init; }
    public required int SubmissionQueueId { get; init; }
    public required int CommandId { get; init; }
    public required int StatusCodeType { get; init; }
    public required int StatusCode { get; init; }
    public required ulong Lba { get; init; }
    public required uint Namespace { get; init; }

    public string CountText => $"#{ErrorCount:N0}";
    public string StatusText => NvmeLogDecoder.StatusText(StatusCodeType, StatusCode);
    public string RawStatusText => $"SCT {StatusCodeType} ／ SC 0x{StatusCode:X2}";
    /// <summary>LBA 只在與媒體相關的錯誤上有意義；其餘型別照實說不適用，不硬印一個數字。</summary>
    public string LbaText => StatusCodeType == 2 ? $"{Lba:N0}" : "—";
    public string NamespaceText => Namespace is 0 or 0xFFFFFFFF ? "—" : $"{Namespace}";
    public string QueueText => $"SQ {SubmissionQueueId} ／ CID {CommandId}";
}

/// <summary>
/// NVMe 紀錄頁的解碼（純函式，不碰硬體，故可完整測試）。
/// </summary>
/// <remarks>
/// <para>
/// <b>位移一律寫成有名字的常數。</b>SMART／健康紀錄（log page 0x02）裡的計數器是 <b>128 位元</b>
/// 而不是 64 位元，而位元組層級的欄位又擠在最前面 7 個位元組裡——這兩件事各錯一次，
/// 就會讓後面每一個欄位都平移，而畫面上仍然是一組「看起來合理」的數字。
/// 1.9.1 之前本專案就是這樣：關鍵警告被當成 2 位元組（規格是 1），把溫度、備用空間、
/// 已使用壽命整批推後一格；128 位元計數器被當成 8 位元組，把寫入量之後的每一個計數器都推錯位置。
/// 同一個檔案裡的 <c>TryReadPowerOnHours</c> 用的是規格上正確的 0x80，兩者對不起來才發現。
/// </para>
/// <para>
/// 所以這裡把整份佈局寫成常數並用合成紀錄釘住：測試要對著<b>規格</b>建資料，
/// 而不是對著實作建資料——後者只會把當下的錯誤鎖起來。
/// </para>
/// </remarks>
public static class NvmeLogDecoder
{
    // ── SMART／健康紀錄（log page 0x02）的佈局。NVMe Base Spec 的
    //    「SMART / Health Information Log Page」，位移單位為位元組。────────────
    public const int OffCriticalWarning = 0x00;     // 1 位元組
    public const int OffCompositeTemp = 0x01;       // 2 位元組（克氏溫度）
    public const int OffAvailableSpare = 0x03;      // 1 位元組（%）
    public const int OffSpareThreshold = 0x04;      // 1 位元組（%）
    public const int OffPercentageUsed = 0x05;      // 1 位元組（%）
    public const int OffDataUnitsRead = 0x20;       // 以下皆為 128 位元（16 位元組）
    public const int OffDataUnitsWritten = 0x30;
    public const int OffHostReadCommands = 0x40;
    public const int OffHostWriteCommands = 0x50;
    public const int OffControllerBusyTime = 0x60;
    public const int OffPowerCycles = 0x70;
    public const int OffPowerOnHours = 0x80;
    public const int OffUnsafeShutdowns = 0x90;
    public const int OffMediaErrors = 0xA0;
    public const int OffErrorLogEntries = 0xB0;

    /// <summary>錯誤資訊紀錄（log page 0x01）每一筆的長度。</summary>
    public const int ErrorEntrySize = 64;

    /// <summary>讀 128 位元計數器的低 64 位元。硬碟壽命內不可能溢出 64 位元，高半部一律為 0。</summary>
    public static ulong Counter128Low(byte[] log, int offset)
    {
        if (log.Length < offset + 8) return 0;
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | log[offset + i];
        return v;
    }

    public static ushort Le16(byte[] b, int o)
        => (ushort)(b.Length >= o + 2 ? b[o] | (b[o + 1] << 8) : 0);

    public static uint Le32(byte[] b, int o)
        => b.Length < o + 4 ? 0u : (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    /// <summary>
    /// 關鍵警告位元組 → 具體是什麼出問題。
    /// </summary>
    /// <remarks>
    /// 這一個位元組原本只以「0x0002」這種原始十六進位顯示，等於沒說。
    /// 每一位元都是一句可行動的話：備用空間見底要準備換碟，唯讀模式代表資料已經寫不進去了。
    /// 未定義的位元照實列出位元號，不假裝認得。
    /// </remarks>
    public static List<NvmeCriticalWarning> CriticalWarnings(byte flags)
    {
        var list = new List<NvmeCriticalWarning>();
        void Add(int bit, string name, string meaning)
        {
            if ((flags & (1 << bit)) != 0) list.Add(new NvmeCriticalWarning(bit, name, meaning));
        }

        Add(0, "備用空間低於門檻", "預留用來替換壞塊的空間已經見底。這是「該準備換碟」最明確的一個訊號。");
        Add(1, "溫度超出臨界範圍", "綜合溫度高於（或低於）廠商設定的臨界值。持續過熱會讓碟自己降速保護。");
        Add(2, "可靠性已降級", "控制器判斷這顆碟的可靠性下降了，通常是媒體錯誤累積或內部自我檢查失敗。");
        Add(3, "已進入唯讀模式", "碟已經被置為唯讀：現在還讀得出來，但寫不進去了。立刻備份，不要重開機碰運氣。");
        Add(4, "揮發性記憶體備份失效", "掉電時用來把快取寫回的備電裝置失效。突然斷電有可能丟掉已回報完成的寫入。");
        Add(5, "持久記憶體區唯讀或不可靠", "持久記憶體區（PMR）已變成唯讀或不可靠。");

        for (int bit = 6; bit < 8; bit++)
            Add(bit, $"未定義的警告位元 {bit}", "本程式的對照表裡沒有這一位元，照實列出而不猜它的意思。");

        return list;
    }

    /// <summary>
    /// 狀態碼 → 人看得懂的一句話。只翻譯查得到的；查不到就回原始 SCT／SC，不編一個聽起來合理的說法。
    /// </summary>
    public static string StatusText(int sct, int sc) => (sct, sc) switch
    {
        (0, 0x00) => "成功完成",
        (0, 0x01) => "無效的命令運算碼",
        (0, 0x02) => "命令欄位無效",
        (0, 0x04) => "資料傳輸錯誤",
        (0, 0x05) => "因掉電而中止",
        (0, 0x06) => "控制器內部錯誤",
        (0, 0x07) => "主機要求中止命令",
        (0, 0x08) => "因提交佇列刪除而中止",
        (0, 0x0B) => "因 Preempt and Abort 而中止",
        (0, 0x0C) => "因 SQ 錯誤而中止",
        (0, 0x80) => "LBA 超出範圍",
        (0, 0x81) => "容量超出限制",
        (2, 0x80) => "寫入故障（Write Fault）",
        (2, 0x81) => "讀取無法修正（Unrecovered Read Error）——這一筆的 LBA 上的資料已經救不回來",
        (2, 0x82) => "端到端保護檢查失敗（Guard Check）",
        (2, 0x83) => "端到端保護檢查失敗（Application Tag）",
        (2, 0x84) => "端到端保護檢查失敗（Reference Tag）",
        (2, 0x85) => "比對失敗（Compare Failure）",
        (2, 0x86) => "存取被拒",
        (2, 0x87) => "讀取未寫入或已解除配置的區塊",
        (7, _) => $"廠商自訂狀態（SC 0x{sc:X2}）",
        _ => $"本程式的對照表裡沒有這個狀態（SCT {sct}／SC 0x{sc:X2}）",
    };

    /// <summary>
    /// 錯誤資訊紀錄（log page 0x01）→ 逐筆。<b>錯誤計數為 0 的槽位是空的</b>，不列出來
    /// ——一整排「#0」只會讓人以為碟上有 64 筆錯誤。
    /// </summary>
    public static List<NvmeErrorEntry> ErrorEntries(byte[] log)
    {
        var list = new List<NvmeErrorEntry>();
        if (log.Length < ErrorEntrySize) return list;

        for (int o = 0; o + ErrorEntrySize <= log.Length; o += ErrorEntrySize)
        {
            ulong count = Counter128Low(log, o);
            if (count == 0) continue;                       // 空槽位

            int status = Le16(log, o + 12);
            list.Add(new NvmeErrorEntry
            {
                ErrorCount = count,
                SubmissionQueueId = Le16(log, o + 8),
                CommandId = Le16(log, o + 10),
                // 狀態欄位：位 0 是 Phase Tag，SC 在位 8:1，SCT 在位 11:9
                StatusCode = (status >> 1) & 0xFF,
                StatusCodeType = (status >> 9) & 0x7,
                Lba = Counter128Low(log, o + 16),
                Namespace = Le32(log, o + 24),
            });
        }

        // 錯誤計數越大越新（那是控制器的單調遞增序號），最新的排前面
        return list.OrderByDescending(e => e.ErrorCount).ToList();
    }
}
