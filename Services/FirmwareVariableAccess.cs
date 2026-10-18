using System;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// <b>讀 UEFI 變數的唯一通路</b>：啟用 <c>SeSystemEnvironmentPrivilege</c>，
/// 再以 <c>GetFirmwareEnvironmentVariableEx</c> 讀一個變數，並把「失敗的<b>真正原因</b>」帶回來。
///
/// <para>
/// <b>為什麼要收斂成一支：</b>2026-10-18 查證時，專案裡有<b>四份</b>啟用權限的實作，
/// 而其中兩份是壞的——這個形狀與本專案修過的其他缺陷同型（<b>同一個錯的多個實例</b>）：
/// </para>
/// <list type="number">
///   <item><see cref="FirmwareService"/>：正確（有查 <c>LastError</c>）。</item>
///   <item><see cref="UefiBootFactsService"/>：正確（同上有查）。</item>
///   <item><see cref="UefiBootOrderFactsService"/>：<b>少查 <c>LastError</c></b>——
///     <c>AdjustTokenPrivileges</c> 在權杖未持有該權限時<b>仍回 true</b>，
///     不查就會把「沒權限讀」當成成功，於是下游把失敗誤報成「變數不存在」。</item>
///   <item><see cref="UefiSignatureFactsService"/>：<b>根本沒有啟用</b>——
///     所以在那台機器上 <c>uefi.db</c>／<c>dbx</c>／<c>kek</c>／<c>pk</c>／
///     <c>uefi.boot_entries.count</c> 全部讀不到，而理由欄寫的是
///     「可能未進入 Setup Mode」——<b>一個沒有查證的因果推測</b>。
///     實測（2026-10-18）該機器的真實錯誤是 <c>1314 ERROR_PRIVILEGE_NOT_HELD</c>，
///     而手動啟用權限之後同一個變數就讀得到（<c>BootOrder</c> 18 bytes、<c>PK</c> 886 bytes）。</item>
/// </list>
///
/// <para>
/// <b>界線：這一支只讀、不寫。</b>它啟用的是一个「讀」的權限，不改任何變數；
/// 專案裡沒有任何 <c>SetFirmwareEnvironmentVariable</c> 的呼叫路徑，這一支也不提供。
/// </para>
///
/// <para>
/// <b>呼叫端不得把 <c>Data is null</c> 一律說成「變數不存在」</b>——
/// <see cref="DescribeFailure"/> 就是為了讓那句話有依據而存在的：
/// 權限不足、變數不存在、其他錯誤，是三種不同的斷言。
/// </para>
/// </summary>
public static class FirmwareVariableAccess
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;

    /// <summary>
    /// 承載 UEFI 變數的兩個命名空間 GUID。
    ///
    /// <para>
    /// <b><c>db</c>／<c>dbx</c> 的 GUID 原本是錯的，而那個錯讓兩把鍵永遠讀不到。</b>
    /// 舊值是 <c>{a7719a85-d6cd-4be1-a1bd-4b0e8bee64c5}</c>——2026-10-18 用兩個 GUID 各讀一次，
    /// 結果是決定性的（見 <c>GATE-LOG-261.md</c> 的實測表）：
    /// </para>
    /// <list type="bullet">
    ///   <item><c>{a7719a85-…}</c>：<c>db</c>／<c>dbx</c>／<c>PK</c>／<c>KEK</c> <b>全部</b>回 0 bytes、
    ///     Win32 <b>203</b>（ERROR_ENVVAR_NOT_FOUND）。</item>
    ///   <item><c>{d719b2cb-3d3a-4596-a3bc-dad00e67656f}</c>：<c>db</c>＝<b>8188</b> bytes、
    ///     <c>dbx</c>＝<b>3724</b> bytes（PK／KEK 在這個 GUID 下不存在，符合預期）。</item>
    ///   <item><c>{8be4df61-…}</c>（Global）：<c>PK</c>＝<b>886</b>、<c>KEK</c>＝<b>3955</b> bytes。✅</item>
    /// </list>
    /// <para>
    /// <b>出處的誠實說明：</b><c>{d719b2cb-…}</c> 是 UEFI 規格的
    /// <c>EFI_IMAGE_SECURITY_DATABASE_GUID</c>，但<b>本機取不到規格原文</b>
    /// （對外 DNS 被指到保留位址，見 <c>STAGE-02-REMAINING-KEYS.md</c> 第四節）。
    /// 所以這一格的主張依據是<b>實測</b>：同一支 API、同一個變數名，只有這個 GUID 讀得到內容。
    /// <b>會判讀錯就說不出來的事我們不做，但這一步是量出來的，不是記出來的。</b>
    /// 規格原文到手後應把出處補成條號。
    /// </para>
    /// </summary>
    public const string GlobalVariableGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    /// <summary>簽章資料庫（<c>db</c>／<c>dbx</c>／<c>dbt</c>）所在的 GUID——見上方的實測表。</summary>
    public const string ImageSecurityGuid = "{d719b2cb-3d3a-4596-a3bc-dad00e67656f}";

    /// <summary>
    /// <b>已知的錯誤值</b>（保留為紀錄，不得再使用）：舊版 <c>db</c>／<c>dbx</c> 用的 GUID。
    /// 留著的理由是讓下一個看到舊資料的人知道「那個數字為什麼會讀不到」，
    /// 而不是把它當成另一個候選值去試。
    /// </summary>
    public const string WrongImageSecurityGuidUsedUntil20261018 = "{a7719a85-d6cd-4be1-a1bd-4b0e8bee64c5}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string name, string guid, byte[]? buffer, uint size, ref uint attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // LUID 是 (uint LowPart, int HighPart)；**寫成 long 會 8-byte 對齊**
    // 讓整個 LUID_AND_ATTRIBUTES 版面錯位，AdjustTokenPrivileges 於是恆敗
    // （本專案已經踩過一次，見 FirmwareService 的註解）。
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    /// <summary><c>ERROR_PRIVILEGE_NOT_HELD</c>：行程權杖沒有（或沒有啟用）那個權限。</summary>
    public const uint ErrorPrivilegeNotHeld = 1314;

    /// <summary><c>ERROR_NOT_ALL_ASSIGNED</c>：<c>AdjustTokenPrivileges</c> 的假成功訊號。</summary>
    public const uint ErrorNotAllAssigned = 1300;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>：那個變數不存在。</summary>
    public const uint ErrorFileNotFound = 2;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>：給的緩衝區不夠（本支會自動放大重試）。</summary>
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>
    /// 一次讀取的完整結果。<c>Data</c> 為 <c>null</c> 時，
    /// <see cref="Win32Error"/> 與 <see cref="PrivilegeEnabled"/> 就是「為什麼讀不到」的全部依據。
    /// </summary>
    public sealed record VariableReadResult(byte[]? Data, uint Win32Error, uint Attributes, bool PrivilegeEnabled)
    {
        /// <summary>這一格是不是「有資料」。</summary>
        public bool HasData => Data is not null;
    }

    /// <summary>
    /// 啟用 <c>SeSystemEnvironmentPrivilege</c>（讀 UEFI 變數的前置條件）。
    ///
    /// <para>
    /// <b>必須查 <c>LastError</c>：</b><c>AdjustTokenPrivileges</c> 在權杖未持有該權限時
    /// <b>回 true 但 LastError＝1300</b>——只收傳回值會得到一個假的成功。
    /// 本專案已經因為這件事錯過一次（把「沒權限讀」誤報成「變數不存在＝可能 Legacy 開機」）。
    /// </para>
    /// </summary>
    public static bool EnablePrivilege()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
                return false;
            try
            {
                if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out Luid luid))
                    return false;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                       && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    /// <summary>
    /// 讀一個 UEFI 變數。
    ///
    /// <para>
    /// <b>大小的取得方式與舊版不同，而舊版是錯的：</b>
    /// <c>GetFirmwareEnvironmentVariableEx</c> 的第五個參數是 <c>pdwAttributes</c>（in/out），
    /// <b>不是大小</b>；以 <c>dwSize＝0</c> 呼叫<b>不會</b>回報所需大小，
    /// 所以「先查大小再讀」的兩段式寫法會永遠拿到 0 而回 null。
    /// 正確做法是直接給足緩衝區，不夠時 Windows 回 122 再放大重試。
    /// </para>
    /// <para>
    /// <paramref name="privilegeEnabled"/> 由呼叫端傳入（同一輪只啟用一次，避免每讀一個變數就動一次權杖）；
    /// <c>null</c> 代表這一支自己去啟用。
    /// </para>
    /// </summary>
    public static VariableReadResult Read(string name, string guid, bool? privilegeEnabled = null)
    {
        bool privileged = privilegeEnabled ?? EnablePrivilege();
        foreach (uint capacity in new uint[] { 64 * 1024, 1024 * 1024 })
        {
            try
            {
                var buffer = new byte[capacity];
                uint attributes = 0;
                uint read = GetFirmwareEnvironmentVariableExW(name, guid, buffer, capacity, ref attributes);
                if (read > 0) return new VariableReadResult(buffer[..(int)read], 0, attributes, privileged);
                uint err = (uint)Marshal.GetLastWin32Error();
                if (err != ErrorInsufficientBuffer)
                    return new VariableReadResult(null, err, attributes, privileged);
            }
            catch (Exception)
            {
                return new VariableReadResult(null, 0, 0, privileged);
            }
        }
        return new VariableReadResult(null, ErrorInsufficientBuffer, 0, privileged);
    }

    /// <summary>
    /// <b>把「為什麼讀不到」寫成一句有依據的話</b>——三種情形分開，不猜。
    /// 這一句會直接進事實的 <c>UnavailableReason</c>。
    /// </summary>
    public static string DescribeFailure(string variableName, VariableReadResult result)
    {
        if (result.HasData) return "";
        if (result.Win32Error == ErrorPrivilegeNotHeld || !result.PrivilegeEnabled)
            return $"{variableName} 讀不到：**行程權杖沒有 SeSystemEnvironmentPrivilege**"
                + $"（Win32 {ErrorPrivilegeNotHeld} ERROR_PRIVILEGE_NOT_HELD）——"
                + "**這不是「變數不存在」、也不是「這台機器不支援 UEFI」**，"
                + "而是這條通路的前置條件沒成立（需管理員；權杖已提權時仍要明確啟用該權限）";
        if (result.Win32Error == 0)
            return $"{variableName} 存在、但長度是 0 bytes（呼叫成功、無資料）——"
                + "**這是「有這個變數但它是空的」，不是「變數不存在」**";
        if (result.Win32Error == ErrorFileNotFound)
            return $"{variableName} 不存在（Win32 {ErrorFileNotFound} ERROR_FILE_NOT_FOUND）——"
                + "這是韌體狀態，不是讀取失敗";
        if (result.Win32Error == ErrorInsufficientBuffer)
            return $"{variableName} 大於本支的緩衝區上限（1 MiB）——**不截斷、不猜內容**";
        return $"{variableName} 讀取失敗（Win32 {result.Win32Error}：{new System.ComponentModel.Win32Exception((int)result.Win32Error).Message}）";
    }

    /// <summary>UEFI 變數屬性的原始值，附上「位元名未對準出處」的界線（見 <c>UNSOURCED-CONSTANTS.md</c> ③）。</summary>
    public static string DescribeAttributes(uint attributes) =>
        $"屬性原始值 0x{attributes:X8}（**位元名未對準出處前不展開**——"
        + "EFI_VARIABLE_* 那張表本專案還沒有可引用的原文；原始值如實列出）";

    /// <summary>給事實用的來源字串（同一支通路，來源敘述不各自表述）。</summary>
    public const string Source = "GetFirmwareEnvironmentVariableEx（UEFI 變數，唯讀）";
}
