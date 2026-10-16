using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 快速開機與休眠狀態（ULTIMATE-1000 階段二 P2-045）。
///
/// <para>
/// <b>為什麼它屬於「影響量測可信度的狀態」而不是一般設定：</b>
/// 快速開機（Fast Startup）讓「關機」實際上是<b>休眠到檔案</b>，而不是重新初始化。
/// 那意味著<b>韌體層面的狀態不會被重建</b>——開機時間、驅動初始化、
/// 甚至某些計數器都會延續上一次的值。任何「開機耗時」或「冷啟動」的量測
/// 在有快速開機的機器上<b>量到的不是它說的那件事</b>。
/// </para>
///
/// <para>
/// <b>界線：本服務只陳述「這個狀態現在是什麼」，不判斷量測是否因此失效。</b>
/// 那個判斷要看被量的是什麼、以及那一次是不是真的走了完整初始化路徑。
/// 把狀態與結論分開，是為了讓「有快速開機」這件事可以在不同的量測裡
/// 各自被納入考量，而不是在這裡被一次性判死。
/// </para>
/// </summary>
public static class BootFastStartupFactsService
{
    private const string Category = "開機";

    public const string FastBootKey = "boot.fastboot";
    public const string HibernateKey = "boot.hibernate";

    /// <summary>注入的輸入——測試不必真的碰登錄檔。</summary>
    public sealed record Inputs(int? HiberbootEnabled, int? HibernateEnabled, int? HiberfilePercent);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Inputs x)
    {
        return
        [
            FastBoot(at, x.HiberbootEnabled),
            Hibernate(at, x.HibernateEnabled, x.HiberfilePercent),
        ];
    }

    private static HardwareFact FastBoot(DateTimeOffset at, int? enabled)
    {
        const string key = FastBootKey;
        const string source = "登錄檔 HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power\\HiberbootEnabled";

        if (enabled is null)
            return new HardwareFact(key, Category, "快速開機", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "登錄裡沒有 HiberbootEnabled——**「沒有這個值」與「它被設為 0」是不同的斷言**："
                + "前者代表這台機器從來沒有這個設定（例如 Legacy 或沒有休眠檔），"
                + "後者代表有人把它關掉了");

        bool on = enabled != 0;
        return new HardwareFact(key, Category, "快速開機",
            on ? "已啟用" : "已停用", "", source, FactTrustLevel.Measured, false, at, enabled,
            on ? FactAvailability.Unknown : FactAvailability.Present,
            on
                ? "**這會影響「開機」量測的意義**：快速開機讓「關機」實際上是休眠到檔案，"
                  + "而不是重新初始化——韌體層面的狀態不會被重建，"
                  + "所以開機時間、驅動初始化與部分計數器會延續上一次的值。"
                  + "**在有快速開機的機器上，任何「冷啟動」量測量到的不是它說的那件事**"
                  + "（本服務只陳述狀態，是否影響某一項量測要看那一次走了哪條路徑）"
                : "已停用——「關機」會走完整的初始化路徑，"
                  + "所以以「開機耗時」為目標的量測在這台機器上量的是它說的那件事");
    }

    private static HardwareFact Hibernate(DateTimeOffset at, int? enabled, int? percent)
    {
        const string key = HibernateKey;
        const string source = "登錄檔 Power\\HibernateEnabled 與 HiberFileSizePercent";

        if (enabled is null)
            return new HardwareFact(key, Category, "休眠", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "登錄裡沒有 HibernateEnabled——通常代表休眠從未被設定過");

        bool on = enabled != 0;
        string value = on ? "已啟用" : "已停用";
        if (on && percent is int p)
            value += $"（休眠檔上限 {p}% 記憶體）";

        return new HardwareFact(key, Category, "休眠", value, "", source,
            FactTrustLevel.Measured, false, at, enabled, FactAvailability.Present,
            on
                ? "休眠與快速開機共用同一個休眠檔——**兩者是同一個機制的兩種用法**："
                  + "一個是使用者按休眠，一個是關機時自動走同一條路"
                : "休眠已停用——但**這不代表快速開機也停用**："
                  + "兩者共用休眠檔，可是停用休眠時 Windows 通常會一併關掉快速開機，"
                  + "反過來則不一定；要看 boot.fastboot 那一項");
    }

    /// <summary>
    /// 從真實系統讀取。每一項各自 try/catch——
    /// 一個值的讀取失敗不該讓另一項也變成「沒有值」。
    /// </summary>
    public static Inputs Read()
    {
        return new Inputs(
            ReadDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"),
            ReadDword(@"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"),
            ReadDword(@"SYSTEM\CurrentControlSet\Control\Power", "HiberFileSizePercent"));
    }

    private static int? ReadDword(string subKey, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            object? v = k?.GetValue(name);
            return v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Diag.Swallow("電源設定讀取", ex, $"{name} 讀不到");
            return null;
        }
    }
}
