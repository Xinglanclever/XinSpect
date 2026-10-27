using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

/// <summary>
/// API 金鑰的落地保護：用 Windows DPAPI（<c>CryptProtectData</c>，目前使用者範圍）加密後
/// 才寫進 <c>settings.json</c>，格式為 <c>dpapi:v1:&lt;base64&gt;</c>。
///
/// <para>
/// 原本金鑰是明文躺在 <c>%APPDATA%\XinSpect\settings.json</c>。那個檔案本來就只有本使用者讀得到，
/// 但明文的意思是：<b>任何能讀到那個檔案的东西都直接拿到金鑰</b>——備份、同步、截圖、
/// 或是另一個以同一使用者身分執行的程式。DPAPI 把「能不能讀檔」與「能不能用金鑰」分開：
/// 換一台機器、換一個帳號，同一份密文就解不開。
/// </para>
/// <para>
/// 三條界線，寫在程式裡也寫在畫面上：①金鑰仍會以明文存在<b>記憶體</b>裡（要拿去簽請求）；
/// ②解不開時一律回「空字串＋原因」，<b>不猜、不退回密文本身當金鑰</b>；
/// ③DPAPI 不支援的環境（非 Windows、API 失敗）就退回明文，但必須如實標示成「明文」，
/// 不能讓畫面顯示「已加密」而檔案裡其實是明文。
/// </para>
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前綴。看到它就一定走解密；沒有它就是舊版的明文（或是本機不支援 DPAPI 時的退路）。</summary>
    public const string Prefix = "dpapi:v1:";

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>這個環境能不能真的加解密（非 Windows 一律 false）。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>設定檔裡的那個字串是不是密文。</summary>
    public static bool LooksProtected(string? stored)
        => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 加密。空字串原樣回傳（沒有東西要保護）。失敗時丟出例外——由呼叫端決定要不要退回明文，
    /// 但<b>不可以在這裡安靜地回明文</b>：那樣呼叫端就分不出「加密成功」與「根本沒加密」。
    /// </summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (!IsSupported) throw new PlatformNotSupportedException("此環境不支援 DPAPI");

        byte[] data = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        IntPtr inPtr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, inPtr, data.Length);
            inBlob.cbData = data.Length;
            inBlob.pbData = inPtr;

            if (!CryptProtectData(ref inBlob, "XinSpect AI API Key", IntPtr.Zero, IntPtr.Zero,
                                  IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                throw new CryptographicException(
                    "DPAPI 加密失敗（Win32 錯誤 " + Marshal.GetLastWin32Error() + "）");

            try
            {
                var encrypted = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);
                return Prefix + Convert.ToBase64String(encrypted);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inPtr); }
    }

    /// <summary>
    /// 解開設定檔裡的金鑰。三種情形嚴格分開：
    /// ①密文且解得開 → 回 true 與明文；
    /// ②沒有前綴 → 視為舊版明文，原樣回傳（<paramref name="wasPlaintext"/> = true）；
    /// ③有前綴但解不開 → 回 false 與原因，<b>不回傳任何金鑰值</b>（不可拿密文去簽請求）。
    /// </summary>
    public static bool TryUnprotect(string? stored, out string value, out string? error, out bool wasPlaintext)
    {
        value = "";
        error = null;
        wasPlaintext = false;

        if (string.IsNullOrEmpty(stored)) return true;

        if (!LooksProtected(stored))
        {
            wasPlaintext = true;
            value = stored;
            return true;
        }

        if (!IsSupported)
        {
            error = "這個環境不支援 DPAPI，無法解開已加密的金鑰";
            return false;
        }

        string b64 = stored[Prefix.Length..];
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(b64); }
        catch (FormatException) { error = "金鑰欄位的編碼損毀（不是有效的 Base64）"; return false; }

        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        IntPtr inPtr = Marshal.AllocHGlobal(encrypted.Length);
        try
        {
            Marshal.Copy(encrypted, 0, inPtr, encrypted.Length);
            inBlob.cbData = encrypted.Length;
            inBlob.pbData = inPtr;

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
            {
                error = "DPAPI 解密失敗（金鑰可能來自另一台電腦或另一個使用者帳號）"
                        + "，Win32 錯誤 " + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                var plain = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                value = Encoding.UTF8.GetString(plain);
                return true;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inPtr); }
    }

    /// <summary>給畫面用的一句話：這個設定檔現在到底怎麼存金鑰（不能含糊）。</summary>
    public static string StorageText(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "未設定金鑰";
        if (LooksProtected(stored)) return "DPAPI 加密（只有本機此使用者帳號解得開）";
        return IsSupported
            ? "明文（下次儲存時會自動改成 DPAPI 加密）"
            : "明文（這個環境不支援 DPAPI）";
    }
}
