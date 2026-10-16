using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 微碼版本雙來源對帳（ULTIMATE-1000 階段二 P2-049）。
///
/// <para>
/// <b>兩個來源，而它們量的是不同階段的同一件事：</b>
/// </para>
/// <list type="bullet">
///   <item><b>MSR 0x8B</b>（IA32_BIOS_SIGN_ID）——<b>現在實際生效的</b>微碼修訂版。
///     這是 CPU 自己回的，不是任何軟體宣告的。</item>
///   <item><b>登錄檔</b> <c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision</c>
///     ——<b>OS 載入時拿到的</b>版本。它是 OS 的紀錄，不是即時讀值。</item>
/// </list>
///
/// <para>
/// <b>兩者不同不必然是錯的：</b>OS 更新微碼之後，登錄值會更新；
/// 但若微碼是 BIOS 在 POST 階段載入的、而 OS 沒有再載入，
/// 兩者就該相同；若 OS 載入了較新的，兩者<b>應該不同</b>，而那個不同正是重點。
/// </para>
///
/// <para>
/// <b>界線：本服務只陳述兩個來源各說了什麼，並列出「相同／不同」。</b>
/// 它<b>不判斷哪一個該生效</b>——那要看載入順序與平台政策，
/// 而那是本服務看不到的部分。
/// </para>
/// </summary>
public static class MicrocodeVersionFactsService
{
    private const string Category = "處理器";

    public const string VersionKey = "mc.version";
    public const string MsrKey = "mc.version.msr";
    public const string RegistryKey = "mc.version.registry";
    public const string CrossKey = "mc.version.cross";

    /// <summary>注入的輸入——測試不必真的讀 MSR 或登錄檔。</summary>
    public sealed record Inputs(uint? MsrRevision, uint? RegistryRevision);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Inputs x)
    {
        return
        [
            Msr(at, x.MsrRevision),
            Registry(at, x.RegistryRevision),
            Cross(at, x.MsrRevision, x.RegistryRevision),
        ];
    }

    private static HardwareFact Msr(DateTimeOffset at, uint? revision)
    {
        const string source = "MSR 0x8B（IA32_BIOS_SIGN_ID）——CPU 自己回的";

        if (revision is null)
            return new HardwareFact(MsrKey, Category, "微碼修訂版（MSR）", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                "讀不到 MSR 0x8B——它需要核心層權限（Ring 0）。"
                + "**這不代表沒有微碼**，只代表這條路現在不通；"
                + "使用者模式讀 MSR 需要驅動，而本專案不安裝驅動");

        uint rev = revision.Value;
        return new HardwareFact(MsrKey, Category, "微碼修訂版（MSR）",
            $"0x{rev:X}", "", source, FactTrustLevel.Measured, false, at, rev,
            rev == 0 ? FactAvailability.Unknown : FactAvailability.Present,
            rev == 0
                ? "MSR 回報 0——**0 不是一個微碼版本**，它代表這個值還沒被寫入"
                  + "（IA32_BIOS_SIGN_ID 在讀取前需要先寫 0，某些情況下會回 0）"
                : "這是**現在實際生效的**修訂版——CPU 自己回的，不是任何軟體宣告的");
    }

    private static HardwareFact Registry(DateTimeOffset at, uint? revision)
    {
        const string source = "登錄檔 HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0\\Update Revision";

        if (revision is null)
            return new HardwareFact(RegistryKey, Category, "微碼修訂版（登錄檔）", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "登錄裡沒有 Update Revision——**「沒有這個值」與「值是 0」是不同的斷言**："
                + "前者代表 OS 沒有記錄微碼載入，後者代表它記錄了一個 0");

        // 登錄的 Update Revision 是 REG_BINARY 的 8 bytes（u64），
        // 高 32 位是 0，真正的修訂版在低 32 位。
        uint rev = revision.Value;
        return new HardwareFact(RegistryKey, Category, "微碼修訂版（登錄檔）",
            $"0x{rev:X}", "", source, FactTrustLevel.Measured, false, at, rev,
            FactAvailability.Present,
            "這是**OS 載入時拿到的**版本——它是 OS 的紀錄，不是即時讀值；"
            + "OS 載入新的微碼之後這個值會更新");
    }

    private static HardwareFact Cross(DateTimeOffset at, uint? msr, uint? reg)
    {
        const string source = "MSR 0x8B × 登錄檔 Update Revision";

        if (msr is null && reg is null)
            return new HardwareFact(CrossKey, Category, "微碼版本對帳", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "兩個來源都讀不到——無從對帳");

        if (msr is null || reg is null)
        {
            string have = msr is not null ? "MSR" : "登錄檔";
            return new HardwareFact(CrossKey, Category, "微碼版本對帳",
                $"只有 {have} 那一邊有值", "", source, FactTrustLevel.Measured, false, at, null,
                FactAvailability.Unknown,
                $"只有 {have} 那一邊讀得到——**這是「無從對照」而不是「不一致」**；"
                + "把一邊缺席算成不一致會製造假的落差");
        }

        bool same = msr.Value == reg.Value;
        return new HardwareFact(CrossKey, Category, "微碼版本對帳",
            same
                ? $"兩邊一致：0x{msr.Value:X}"
                : $"MSR 0x{msr.Value:X}；登錄檔 0x{reg.Value:X}"
                  + (msr.Value > reg.Value ? "（MSR 較新）"
                     : msr.Value < reg.Value ? "（登錄檔較新）" : ""),
            "", source, FactTrustLevel.Measured, false, at, same ? 1 : 0,
            same ? FactAvailability.Present : FactAvailability.Unknown,
            same
                ? "兩邊相同——**這不代表微碼來自 BIOS**：也可能是 OS 載入了與 BIOS 相同的那一版"
                : "**兩者不同不必然是錯的。**OS 更新微碼之後登錄值會更新，"
                  + "而 MSR 反映的是現在生效的。哪一個「該」生效要看載入順序與平台政策——"
                  + "**那是本服務看不到的部分，所以它只列出差異，不判斷誰對**");
    }

    /// <summary>
    /// 讀取兩個來源。<b>兩者各自 try/catch</b>——
    /// 一個讀不到不該讓另一個也變成「沒有值」。
    /// </summary>
    public static Inputs Read()
    {
        return new Inputs(ReadMsr(), ReadRegistry());
    }

    /// <summary>
    /// 讀 MSR 0x8B。<b>使用者模式讀不到</b>，所以這裡如實回 null——
    /// 本專案不為了讀一個值而安裝驅動。
    /// </summary>
    private static uint? ReadMsr()
    {
        try
        {
            // 先寫 0 是 IA32_BIOS_SIGN_ID 的協定：寫入後再讀才拿到修訂版。
            // 但使用者模式沒有 WRMSR 的途徑，所以這裡只能如實承認讀不到。
            return null;
        }
        catch (Exception ex)
        {
            Diag.Swallow("微碼 MSR 讀取", ex, "MSR 讀不到");
            return null;
        }
    }

    private static uint? ReadRegistry()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            object? v = k?.GetValue("Update Revision");
            if (v is byte[] b)
            {
                // 8 bytes little-endian；取低 32 位。
                return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            }
            if (v is not null) return Convert.ToUInt32(v, CultureInfo.InvariantCulture);
            return null;
        }
        catch (Exception ex)
        {
            Diag.Swallow("微碼登錄讀取", ex, "Update Revision 讀不到");
            return null;
        }
    }
}
