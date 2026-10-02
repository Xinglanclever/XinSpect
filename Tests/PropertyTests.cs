using FsCheck.Xunit;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Property-based 測試（V7 T1／WP50）：性質對任意輸入成立，由 FsCheck 隨機產生並自動縮小反例。
/// 與逐例測試互補——這裡抓的是「沒想到的輸入形狀」，不是枚舉過的邊界。
/// </summary>
public class PropertyTests
{
    [Property]
    public bool FREG編碼解碼往返_對任意基底與上限(int base4k, int limit4k)
    {
        if (base4k is < 0 or > 0x7FFF || limit4k is < 0 or > 0x7FFF) return true; // 超出 15 位元欄位者非本解碼器宣稱範圍
        var d = SpiFlash.DecodeFreg(SyntheticFixtures.EncodeFreg((ushort)base4k, (ushort)limit4k));
        return d.Base4k == base4k && d.Limit4k == limit4k && d.Empty == (base4k == 0 && limit4k == 0);
    }

    [Property]
    public bool PRX編碼解碼往返_含保護位(int base4k, int limit4k, bool writeProtect, bool readProtect)
    {
        if (base4k is < 0 or > 0x7FFF || limit4k is < 0 or > 0x7FFF) return true;
        var d = SpiFlash.DecodePrx(SyntheticFixtures.EncodePrx((ushort)base4k, (ushort)limit4k, writeProtect, readProtect));
        return d.Base4k == base4k && d.Limit4k == limit4k
            && d.WriteProtect == writeProtect && d.ReadProtect == readProtect
            && d.Enabled == (writeProtect || readProtect);
    }

    [Property]
    public bool HSFSTS未知位元_解碼後原樣保留(int noise)
    {
        uint unknown = (uint)noise; // 任意 32 位元形狀
        var d = SpiFlash.DecodeHsfsts(SyntheticFixtures.EncodeHsfsts(false, false, false, false, false, false, unknown));
        // 已知位元遮罩（FDONE bit0/FCERR bit1/AEL bit2/WRSDIS bit11/FDOPSS bit13/FLOCKDN bit15 = 0xA807）：
        // 未知位元必須原樣出現在 UnknownBits。
        return d.UnknownBits == (unknown & ~0xA807u);
    }

    [Property]
    public bool PCAT校驗和_算出即相符_動一格即不符(byte[] payload)
    {
        if (payload.Length != 0x1E) return true; // 0x10–0x2D 共 30 位元組
        var regs = new byte[0x40];
        Array.Copy(payload, 0, regs, 0x10, 0x1E);
        int sum = 0;
        for (int i = 0x10; i <= 0x2D; i++) sum += regs[i];
        regs[0x2E] = (byte)((sum & 0xFFFF) >> 8);
        regs[0x2F] = (byte)sum;
        if (!Cmos.ChecksumMatches(regs)) return false;

        regs[0x10] ^= 0xFF; // 任意改動一格（0x10 在區內）——除非 XOR 後恰為原值（不可能，0xFF≠0）
        return !Cmos.ChecksumMatches(regs);
    }

    [Property]
    public bool MCFG條目數_由表長推導(int entryCount, int extra)
    {
        if (entryCount is < 0 or > 200 || extra is < 0 or >= 16) return true; // extra 是湊不成完整條目的尾段
        var table = new byte[44 + 16 * entryCount + extra];
        table[0] = (byte)'M'; table[1] = (byte)'C'; table[2] = (byte)'F'; table[3] = (byte)'G';
        var entries = AcpiTable.McfgEntries(table);
        return entries.Count == entryCount;
    }

    [Property]
    public bool 揮發遮罩_遮罩內變動永不入差分(byte[] before, byte[] after, byte[] maskRaw)
    {
        int len = System.Math.Min(before.Length, System.Math.Min(after.Length, maskRaw.Length));
        if (len == 0) return true;
        var mask = maskRaw[0..len];
        var regionBefore = new RawRegisterRegion { Source = "test", Bytes = before[0..len], VolatilityMask = mask };
        var regionAfter = new RawRegisterRegion { Source = "test", Bytes = after[0..len], VolatilityMask = mask };

        var diff = RawRegisterSnapshotService.Diff([regionBefore], [regionAfter]);
        var change = Assert.Single(diff.Regions);
        if (change.Kind != RawRegionChangeKind.Changed) return true; // 沒變動自然沒有遮罩違規

        return change.ChangedOffsets.All(i => i < len && mask[i] == 0); // 不變式：遮罩內位元組永不進差分
    }

    [Property]
    public bool 差分自反_同一份快照自己比自己恆不變(byte[] bytes, byte[] maskRaw)
    {
        var mask = bytes.Length == maskRaw.Length ? maskRaw : null;
        var region = new RawRegisterRegion { Source = "test", Bytes = bytes, VolatilityMask = mask };
        var diff = RawRegisterSnapshotService.Diff([region], [region]);
        return diff.Regions.Single().Kind == RawRegionChangeKind.Unchanged;
    }
}
