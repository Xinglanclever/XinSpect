using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// CPU 韌體身分事實（供交叉對帳 WP5）：目前生效微碼修訂版的兩個獨立視角——
/// CPU 自己說的（MSR 0x8B bits[63:32]，逐實體核綁定讀取）與 Windows 說的
/// （登錄檔 Update Revision）——加上 TjMax（MSR 0x1A2 bits[23:16]）。
/// 讀不到標三態；微碼逐核不一致時誠實標 ReadError 帶逐核清單，不取第一核冒充全機。
/// </summary>
public static class CpuFirmwareFactsService
{
    private const string Category = "韌體安全";
    private const uint MsrBiosSignId = 0x8B;
    private const uint MsrTemperatureTarget = 0x1A2;

    public static IReadOnlyList<HardwareFact> Collect(IKernelMsrReader msr, DateTimeOffset at,
        Func<byte[]?>? registryProbe = null)
    {
        byte[]? reg = (registryProbe ?? ReadUpdateRevision)();
        if (!msr.Available)
        {
            string reason = msr.UnavailableReason ?? "缺 ring0：特權讀取未就緒";
            return [MicrocodeMsrFact(at, [], reason), MicrocodeRegistryFact(at, reg), TjMaxFact(at, null, reason)];
        }

        var revisions = new List<uint?>();
        foreach (var core in CpuAffinity.PhysicalCores(CpuAffinity.IsMultiGroup, ulong.MaxValue))
        {
            using var pin = CpuAffinity.Pinned(core.First); // 綁到該實體核，RDMSR 才讀得到那顆核的值
            revisions.Add(msr.ReadMsr(MsrBiosSignId) is { } sign ? (uint)(sign >> 32) : null);
        }
        ulong? tempTarget = msr.ReadMsr(MsrTemperatureTarget);
        return [MicrocodeMsrFact(at, revisions, null), MicrocodeRegistryFact(at, reg), TjMaxFact(at, tempTarget, null)];
    }

    /// <summary>由逐核讀值組成微碼事實：全核一致才給值；不一致或全失敗誠實標三態。</summary>
    public static HardwareFact MicrocodeMsrFact(DateTimeOffset at, IReadOnlyList<uint?> revisions, string? unavailableReason)
    {
        const string key = "msr.0x8b", name = "微碼修訂版（MSR 0x8B）",
            source = "MSR 0x8B（IA32_BIOS_SIGN_ID）bits[63:32]，逐實體核綁定讀取";
        if (unavailableReason is not null)
            return Unavailable(key, name, source, at, FactAvailability.InsufficientPrivilege, unavailableReason);
        var known = revisions.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (known.Count == 0)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, "0x8B 逐核皆讀取失敗");
        var groups = known.GroupBy(r => r).ToList();
        if (groups.Count > 1)
            return Unavailable(key, name, source, at, FactAvailability.ReadError,
                "逐核不一致（" + string.Join("、", groups.Select(g => $"0x{g.Key:X8}×{g.Count()}")) + "）——不取單核冒充全機");
        uint rev = groups[0].Key;
        return new HardwareFact(key, Category, name, $"0x{rev:X8}", "", source, FactTrustLevel.Measured, false, at, rev);
    }

    /// <summary>
    /// 解碼登錄檔「Update Revision」（8 位元組）。文獻對修訂版落在哪個 DWORD 說法不一
    /// （offset 0 與 offset 4 都有人引用）——這裡不猜：兩個 DWORD 恰一個非零就取那個，
    /// 皆非零標佈局歧義（原始 hex 一併附上可稽核），皆零視為「可能未載入」。
    /// </summary>
    public static HardwareFact MicrocodeRegistryFact(DateTimeOffset at, byte[]? raw)
    {
        const string key = "reg.microcode", name = "微碼修訂版（登錄檔）",
            source = "登錄檔 CentralProcessor\\0「Update Revision」";
        if (raw is null)
            return Unavailable(key, name, source, at, FactAvailability.NotSupported, "登錄值不存在");
        if (raw.Length < 8)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, $"格式不明（{raw.Length} 位元組，須 8）");
        uint d0 = BitConverter.ToUInt32(raw, 0), d4 = BitConverter.ToUInt32(raw, 4);
        string hex = Convert.ToHexString(raw);
        if (d0 != 0 && d4 != 0)
            return Unavailable(key, name, source, at, FactAvailability.ReadError,
                $"佈局歧義（兩個 DWORD 皆非零：0x{d0:X8}/0x{d4:X8}）——原始 {hex}，不解碼");
        uint rev = d0 != 0 ? d0 : d4;
        string note = rev == 0 ? "兩個 DWORD 皆 0：可能未載入任何微碼修訂" : $"原始 {hex}";
        return new HardwareFact(key, Category, name, $"0x{rev:X8}（{note}）", "", source, FactTrustLevel.Reported, false, at, rev);
    }

    /// <summary>TjMax＝MSR 0x1A2 bits[23:16]。AMD／未實作平台讀失敗時誠實三態（合理性檢查由對帳規則負責）。</summary>
    public static HardwareFact TjMaxFact(DateTimeOffset at, ulong? raw, string? unavailableReason)
    {
        const string key = "cpu.tjmax", name = "TjMax 目標溫度",
            source = "MSR 0x1A2（IA32_TEMPERATURE_TARGET）bits[23:16]";
        if (unavailableReason is not null)
            return new HardwareFact(key, "處理器", name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.InsufficientPrivilege, unavailableReason);
        if (raw is null)
            return new HardwareFact(key, "處理器", name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "平台未實作或讀取失敗");
        uint tj = (uint)((raw.Value >> 16) & 0xFF);
        return new HardwareFact(key, "處理器", name, tj.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "°C", source, FactTrustLevel.Measured, false, at, tj);
    }

    /// <summary>讀 Windows 記錄的微碼修訂（HKLM\HARDWARE\...\CentralProcessor\0「Update Revision」）；不存在或失敗回 null。</summary>
    public static byte[]? ReadUpdateRevision()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("Update Revision") as byte[];
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
