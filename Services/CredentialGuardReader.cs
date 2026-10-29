using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// Credential Guard 設定面的唯讀讀取（U10 第三圈 SE-0005，第 319 輪）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要獨立一支：</b>純函式（<see cref="CredentialGuardFactsService"/>）不碰登錄檔，
/// 所以「值不存在」與「登錄檔讀不到」這兩件事必須在這裡就分開：
/// 值不存在＝<b>未設定</b>（政策事實，回 <c>null</c> 欄位）；
/// 讀取拋例外＝<b>讀不到</b>（回整份 <c>null</c>＋理由，事實層標 <c>ReadError</c>）。
/// 把兩者壓成「0」是這個專案踩過五次的同一種錯。
/// </para>
/// <para>
/// <b>三個值各自的出處：</b><c>LsaCfgFlags</c> 在 <c>Control\Lsa</c>（0 停用／1 啟用含 UEFI 鎖／
/// 2 啟用不含鎖）；<c>Scenarios\CredentialGuard\Enabled</c> 與 <c>EnableVirtualizationBasedSecurity</c>
/// 在 <c>Control\DeviceGuard</c>。三處都只讀不寫。
/// </para>
/// </remarks>
public static class CredentialGuardReader
{
    /// <summary>讀一次設定面；<c>Config</c> 為 <c>null</c> 表示讀取本身失敗（理由在 <c>Reason</c>）。</summary>
    public static (CredentialGuardFactsService.Config? Config, string? Reason) TryRead()
    {
        try
        {
            return (new CredentialGuardFactsService.Config(
                ReadDword(@"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags"),
                ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\CredentialGuard", "Enabled"),
                ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity")), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>讀一個 DWORD；<b>鍵或值不存在回 <c>null</c></b>（＝未設定），不是 0。</summary>
    internal static uint? ReadDword(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        if (key is null) return null;
        object? raw = key.GetValue(name);
        if (raw is null) return null;
        return raw switch
        {
            int i => unchecked((uint)i),
            uint u => u,
            long l => unchecked((uint)l),
            string s when uint.TryParse(s, out uint parsed) => parsed,
            _ => null,
        };
    }
}
