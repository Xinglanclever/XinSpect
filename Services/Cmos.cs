namespace XinSpect;

/// <summary>
/// CMOS/RTC 的純解碼器（V7 WP6／A10）。輸入是已讀出的暫存器快照（0x00–0x3F），
/// 不碰 I/O 埠——讀取協定（寫 0x70 選址、讀 0x71）在服務層。佈局界線：0x10–0x2D 的
/// 廠商 BIOS 設定區「可讀但刻意不解碼」（佈局依機種而異，未對準規格前任何解讀都違反誠實原則）；
/// 唯一跨機種的標準是 PC-AT 校驗和（0x2E/0x2F 覆蓋 0x10–0x2D），照實比對、附但書。
/// </summary>
public static class Cmos
{
    [SpecRef("Motorola MC146818／Intel PCH RTC 暫存器 0x0D：bit7 VRT（Valid RAM and Time）＝1 代表電池供電正常")]
    public static bool VrtValid(byte reg0D) => (reg0D & 0x80) != 0;

    [SpecRef("Motorola MC146818：0x0A bit7 UIP（更新進行中）；0x0B bit2 DM（1＝二進位、0＝BCD）、bit1 MIL／24/12（1＝24 小時制）")]
    public static (bool UpdateInProgress, bool BinaryMode, bool Hour24) DecodeStatus(byte reg0A, byte reg0B) =>
        ((reg0A & 0x80) != 0, (reg0B & 0x04) != 0, (reg0B & 0x02) != 0);

    [SpecRef("Motorola MC146818：0x00 秒、0x02 分、0x04 時；BCD 為預設格式；12 小時制時 bit7＝PM")]
    public static (int Hour, int Minute, int Second)? DecodeTime(byte sec, byte min, byte hour, bool binary, bool hour24)
    {
        int? s = binary ? sec : FromBcd(sec);
        int? m = binary ? min : FromBcd(min);
        int? h = binary ? hour : FromBcd((byte)(hour & 0x7F));
        if (s is null || m is null || h is null) return null;
        int hour24Value = h.Value;
        if (!hour24)
        {
            bool pm = (hour & 0x80) != 0;
            hour24Value = hour24From12(h.Value, pm);
        }
        if (hour24Value > 23 || m.Value > 59 || s.Value > 59) return null;
        return (hour24Value, m.Value, s.Value);
    }

    /// <summary>PC-AT 校驗和：0x10–0x2D 逐位元組總和應等於 0x2E（高位）+0x2F（低位）。BIOS 不維護此區的機種會不符——但書由呼叫端帶。</summary>
    [SpecRef("IBM PC/AT 技術參考：CMOS 0x2E/0x2F 為 0x10–0x2D 逐位元組總和的 16 位元校驗和（高位在前）")]
    public static bool ChecksumMatches(ReadOnlySpan<byte> regs)
    {
        int sum = 0;
        for (int i = 0x10; i <= 0x2D; i++) sum += regs[i];
        sum &= 0xFFFF;
        int stored = (regs[0x2E] << 8) | regs[0x2F];
        return sum == stored;
    }

    private static int hour24From12(int hour, bool pm)
    {
        int h12 = hour % 12;        // 12 點歸零：AM 12→0（午夜）、PM 12→12（正午）
        return pm ? h12 + 12 : h12;
    }

    private static int? FromBcd(byte b) => (b & 0x0F) > 9 || (b >> 4) > 9 ? null : (b & 0x0F) + (b >> 4) * 10;
}
