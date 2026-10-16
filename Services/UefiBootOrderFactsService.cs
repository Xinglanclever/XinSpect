using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 開機順序事實（ULTIMATE-1000 階段二 P2-042）與 UEFI 載入選項欄位事實（P2-035 的開端）。
///
/// <para>
/// <b>為什麼這一項值得獨立於既有的 <c>uefi.boot_order_count</c>：</b>
/// 那把鍵只報「有幾筆」，而開機順序的<b>內容</b>（是哪幾個裝置、誰在前）
/// 才是回答「為什麼它從這裡開機」所需要的。
/// </para>
///
/// <para>
/// <b>界線，而這是本項最重要的一句：</b>
/// <b>UEFI 變數裡的順序不等於實際的開機結果。</b>
/// 韌體可以（而且常常）跳過順序裡的前幾項——開機失敗會往下找、
/// 使用者按了快速鍵會直接選、而某些平台會重排。
/// 本服務只陳述<b>變數裡寫了什麼</b>，不推論「所以下一次會從哪裡開」。
/// </para>
/// </summary>
public static class UefiBootOrderFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI 變數（GetFirmwareEnvironmentVariableEx）";
    private const string EfiGlobal = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
    private const string EfiBootPrefix = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    public const string OrderKey = "fw.bootorder";
    public const string LoadOptionsKey = "uefi.loadopt.count";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid,
        byte[]? buffer, uint size, ref uint attributes);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008, SePrivilegeEnabled = 0x0002;
    private struct Luid { public uint LowPart; public int HighPart; }
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    /// <summary>
    /// 收集開機順序事實。<paramref name="readOrder"/> 注入＝測試路徑；
    /// <paramref name="loadOptionCounts"/> 是「每個載入選項名稱對應的內容位元組數」。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<byte[]?>? readOrder = null,
        Func<string, int?>? readLoadOptionLength = null,
        bool? privilegeOk = null)
    {
        bool hasInjected = readOrder is not null || readLoadOptionLength is not null || privilegeOk is not null;
        bool privileged = privilegeOk ?? (hasInjected || EnableFirmwarePrivilege());

        byte[]? order = privileged ? (readOrder ?? (() => ReadVariable("BootOrder", 512)))() : null;

        var facts = new List<HardwareFact>
        {
            OrderFact(at, order, privileged),
        };

        if (order is not null)
        {
            var loader = readLoadOptionLength ?? (name => ReadVariable(name, 4096)?.Length);
            facts.Add(LoadOptionFact(at, order, loader));
        }
        else
        {
            facts.Add(new HardwareFact(LoadOptionsKey, Category, "UEFI 載入選項",
                "", "個", Source, FactTrustLevel.Unknown, false, at, null,
                privileged ? FactAvailability.NotApplicable : FactAvailability.InsufficientPrivilege,
                privileged
                    ? "沒有 BootOrder 可以走訪——載入選項是從它列出的項目去問的"
                    : "無法啟用 SeSystemEnvironmentPrivilege（需管理員；或本機為 Legacy 開機）"));
        }

        return facts;
    }

    /// <summary>
    /// 開機順序：BootOrder 是 u16 的序列（little-endian），每個值對應一個 BootXXXX 變數。
    /// </summary>
    private static HardwareFact OrderFact(DateTimeOffset at, byte[]? raw, bool privileged)
    {
        if (!privileged)
            return new HardwareFact(OrderKey, Category, "開機順序", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                "無法啟用 SeSystemEnvironmentPrivilege（需管理員；或本機為 Legacy 開機）");

        // 「取不到變數」與「變數存在但是空的」是不同的斷言——
        // 第一版把 raw is null 與 raw.Length == 0 併在同一個判斷裡，
        // **而訊息文字寫對了、邏輯卻做錯了**：訊息說「兩者不同」，
        // 判斷卻把它們當成同一件事。只有測試能發現這件事，
        // 因為那段訊息讀起來是對的。
        if (raw is null)
            return new HardwareFact(OrderKey, Category, "開機順序", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "取不到 BootOrder 變數——Legacy 開機的機器沒有這個變數，"
                + "而「沒有這個變數」與「順序是空的」是不同的斷言");

        if (raw.Length % 2 != 0)
            return new HardwareFact(OrderKey, Category, "開機順序", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                $"BootOrder 長度 {raw.Length} 不是偶數——它是 u16 的序列，"
                + "奇數長度代表這個變數被截斷或格式不符，如實拒解");

        var entries = new List<ushort>();
        for (int i = 0; i + 1 < raw.Length; i += 2)
            entries.Add((ushort)(raw[i] | (raw[i + 1] << 8)));

        string value = entries.Count == 0
            ? "順序是空的"
            : string.Join(" → ", entries.Select(e => $"Boot{e:X4}"));

        return new HardwareFact(OrderKey, Category, "開機順序", value, "筆", Source,
            FactTrustLevel.Measured, false, at, entries.Count,
            entries.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
            entries.Count == 0
                ? "BootOrder 存在但沒有項目——那是「順序裡沒有東西」而不是「取不到順序」"
                : "**這是變數裡寫的順序，不是實際的開機結果。**"
                  + "韌體可以（而且常常）跳過前幾項：開機失敗會往下找、"
                  + "使用者按了快速鍵會直接選、某些平台還會重排。"
                  + "要回答「上次從哪裡開」需要開機事件記錄那個獨立來源");
    }

    /// <summary>
    /// 走訪順序裡列出的每一個載入選項，量它的內容長度。
    /// <b>不解析內容</b>——載入選項的內容是裝置路徑與選用資料的串接，
    /// 逐欄位拆解需要另一套規格圖（那是 P2-035 的範圍）。
    /// </summary>
    private static HardwareFact LoadOptionFact(DateTimeOffset at, byte[] raw,
        Func<string, int?> readLength)
    {
        var entries = new List<ushort>();
        for (int i = 0; i + 1 < raw.Length; i += 2)
            entries.Add((ushort)(raw[i] | (raw[i + 1] << 8)));

        var found = new List<string>();
        var missing = new List<string>();

        foreach (ushort e in entries)
        {
            string name = $"Boot{e:X4}";
            int? len = readLength(name);
            if (len is int n && n > 0) found.Add($"{name}（{n} bytes）");
            else missing.Add(name);
        }

        string value = found.Count == 0
            ? $"順序裡的 {entries.Count} 個選項都取不到內容"
            : string.Join("、", found)
              + (missing.Count > 0 ? $"；取不到：{string.Join("、", missing)}" : "");

        return new HardwareFact(LoadOptionsKey, Category, "UEFI 載入選項",
            value, "個", Source, FactTrustLevel.Measured, false, at, found.Count,
            found.Count == 0 ? FactAvailability.Unknown : FactAvailability.Present,
            missing.Count > 0
                ? $"有 {missing.Count} 個選項取不到——它們可能已被刪除，"
                  + "也可能是權限不足；**這兩者在這裡分不出來**，所以要另外查"
                : "本服務只量內容長度**不解析內容**——載入選項的內容是裝置路徑"
                  + "與選用資料的串接，逐欄位拆解需要另一套規格圖（列在 P2-035）");
    }

    private static byte[]? ReadVariable(string name, uint size)
    {
        try
        {
            var buf = new byte[size];
            uint attr = 0;
            uint ret = GetFirmwareEnvironmentVariableEx(name, EfiGlobal, buf, size, ref attr);
            return ret > 0 && ret <= size ? buf[..(int)ret] : null;
        }
        catch { return null; }
    }

    private static bool EnableFirmwarePrivilege()
    {
        try
        {
            if (!OpenProcessToken(-1, TokenAdjustPrivileges | TokenQuery, out nint token)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out Luid luid))
                    return false;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                return AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0);
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }
}
