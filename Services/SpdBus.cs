namespace XinSpect;

/// <summary>
/// 能讀 SPD 的匯流排。<b>刻意只有 SPD 需要的那幾個動作</b>——不是通用的 SMBus 介面。
/// </summary>
/// <remarks>
/// 兩個實作：<see cref="SmbusController"/>（PCH 的 i801，主流桌上平台）與
/// <see cref="ImcSmbusController"/>（處理器記憶體控制器自己的 SMBus，HEDT／伺服器平台）。
/// <see cref="SpdReader"/> 只認這個介面，所以兩條路徑共用同一套讀取、切頁與判讀邏輯，
/// 也共用同一組「全 0／全 F 判讀不到」「讀完復位頁」的規則。
/// </remarks>
public interface ISpdBus
{
    /// <summary>取得匯流排的獨占權。取不到就必須放棄——不搶。</summary>
    bool TryAcquireBus(out string reason);

    /// <summary>歸還獨占權，並把借用期間改過的控制器狀態還原。</summary>
    void ReleaseBus();

    /// <summary>讀 SPD EEPROM 的一個位元組。讀不到回 <c>null</c>。</summary>
    byte? ReadByteData(byte slave7, byte command);

    /// <summary>對切頁裝置發一個位元組（DDR4 的 SPA0／SPA1）。</summary>
    bool SendByte(byte slave7, byte data);

    /// <summary>最後一次失敗的原因（人看得懂的中文）。</summary>
    string LastError { get; }

    /// <summary>最後一次交易的結果分類。</summary>
    SmbusStatus LastStatus { get; }

    /// <summary>這條匯流排是什麼、在哪裡——寫進事實的血統欄用。</summary>
    string Description { get; }
}

/// <summary>
/// SPD 匯流排上<b>唯一</b>允許碰的裝置位址。兩個控制器實作共用這一份，測試也只釘這一份。
/// </summary>
/// <remarks>
/// <para>
/// 這是整個 SPD 直讀功能的安全底線。可以讀的只有 EEPROM 的 0x50–0x57；可以寫的只有
/// DDR4 的兩個切頁位址 0x36／0x37。
/// </para>
/// <para>
/// 線的另一邊是什麼：<b>0x31／0x34／0x35 是 SWP0–2</b>（把 SPD 永久寫入保護）、
/// <b>0x33 是 CWP</b>（清除保護）。送錯一次就可能讓那條記憶體再也開不了機，
/// 而且沒有軟體層的復原方式。所以這裡不是「檢查後拒絕」，是<b>那條路徑不存在</b>：
/// 控制器實作只接受通過這兩個判斷的位址，其餘一律拋例外。
/// </para>
/// </remarks>
public static class SpdBusAddresses
{
    /// <summary>SPD EEPROM 的八個裝置位址：0x50–0x57。</summary>
    public static bool IsSpdRead(byte slave7) => slave7 is >= 0x50 and <= 0x57;

    /// <summary>DDR4 的兩個切頁位址（SPA0＝0x36、SPA1＝0x37）。</summary>
    public static bool IsPageSelect(byte slave7) => slave7 is 0x36 or 0x37;

    /// <exception cref="ArgumentOutOfRangeException">位址不在 SPD EEPROM 白名單內。</exception>
    public static void EnsureSpdRead(byte slave7)
    {
        if (!IsSpdRead(slave7))
            throw new ArgumentOutOfRangeException(nameof(slave7), slave7,
                "只允許讀取 SPD EEPROM 的 0x50–0x57；其餘裝置位址不在白名單內。");
    }

    /// <exception cref="ArgumentOutOfRangeException">位址不是 0x36／0x37。</exception>
    public static void EnsurePageSelect(byte slave7)
    {
        if (!IsPageSelect(slave7))
            throw new ArgumentOutOfRangeException(nameof(slave7), slave7,
                "只允許對 DDR4 切頁位址 0x36／0x37 寫入。"
                + "SPD 的寫入保護指令（SWP0–2＝0x31／0x34／0x35、CWP＝0x33）與 EEPROM 資料區永不寫入。");
    }
}
