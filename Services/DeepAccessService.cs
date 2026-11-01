using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace XinSpect;

/// <summary>憑證信任庫的接縫：裝/查/移 CA 信任（LocalMachine 的 Root 與 TrustedPublisher）。測試注入假件，不在單測碰真實信任庫。</summary>
public interface ICertTrustStore
{
    bool IsTrusted(string thumbprint);
    void InstallTrusted(X509Certificate2 publicCertificate);
    void RemoveTrusted(string thumbprint);
}

/// <summary>核心驅動服務控制接縫（SERVICE_KERNEL_DRIVER、手動啟動）。測試注入假件。</summary>
public interface IDriverServiceControl
{
    DriverServiceState QueryState(string serviceName);
    void Install(string serviceName, string sysPath);
    void Start(string serviceName);
    void StopAndDelete(string serviceName);
}

/// <summary>驅動簽章接縫：用本機產生的 CA 簽 XsRegProbe.sys。測試注入假件，不在單測叫 signtool。</summary>
public interface ISysSigner
{
    bool Sign(string sysPath, string pfxPath, string password, out string message);
}

public enum DriverServiceState { NotFound, Stopped, Running, Unknown }

public sealed record DeepAccessStatus(
    bool CaTrusted,
    DriverServiceState DriverState,
    bool SysPresent,
    bool IsEnabled,
    IReadOnlyList<string> Notes);

/// <summary>
/// 深層核心存取豁免開關（深層暫存器計畫 §5.4）。
/// 開＝確保自簽 CA 存在（沒有就產生並存到 ProgramData）→ 裝進信任庫 → .sys 在時安裝並啟動 XsRegProbe 服務；
/// 關＝停止並刪除服務 → 只移除「本專案 CA 那一張」的信任（以存檔 thumbprint 為準，不掃庫、不碰別人的根）。
/// 非提權一律拒做並如實說明；部分失敗不謊稱成功。驅動唯讀、退出即卸載，與 BlueSquadron 的常駐保護相反。
/// 邏輯經假 store／假服務單測；真實 X509TrustStore 與 ScmDriverService 只做最薄的系統呼叫。
/// </summary>
public sealed class DeepAccessService : ObservableObject
{
    public const string ServiceName = "XsRegProbe";
    public const string CaSubject = "CN=XinSpect Driver CA";
    public const string SignSubject = "CN=XinSpect Driver Signing";
    private const int KeyBits = 4096;

    private readonly ICertTrustStore _trustStore;
    private readonly IDriverServiceControl _service;
    private readonly string _artifactDir;
    private readonly bool _isElevated;
    private readonly Func<string?> _handshakeProbe;
    private readonly ISysSigner _signer;

    public DeepAccessService(ICertTrustStore? trustStore = null, IDriverServiceControl? serviceControl = null,
        string? artifactDir = null, bool? isElevated = null, Func<string?>? handshakeProbe = null,
        ISysSigner? signer = null)
    {
        _trustStore = trustStore ?? new X509TrustStore();
        _service = serviceControl ?? new ScmDriverService();
        _artifactDir = artifactDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XinSpect", "Driver");
        _isElevated = isElevated ?? IsCurrentUserElevated();
        _handshakeProbe = handshakeProbe ?? DefaultHandshakeProbe;
        _signer = signer ?? new SigntoolSigner();
        RefreshStatus();
    }

    /// <summary>真實握手探測：開 \\.\XsRegProbe 走 QUERY_INFO 對帳（已測邏輯）；通過回描述文字，不通過回 null。</summary>
    private static string? DefaultHandshakeProbe()
    {
        try
        {
            using var reader = new DriverMmioReader();
            return reader.Available ? "裝置握手通過（能力協商與允許清單對帳成功）" : null;
        }
        catch { return null; }
    }

    public string SysPath => Path.Combine(_artifactDir, "XsRegProbe.sys");
    public string CaCerPath => Path.Combine(_artifactDir, "XinSpectCA.cer");
    private string CaPfxPath => Path.Combine(_artifactDir, "XinSpectCA.pfx");
    private string CaKeyPath => Path.Combine(_artifactDir, "XinSpectCA.pfxkey");
    // 簽 .sys 用的是「由 CA 簽出的葉憑證」，不是 CA 本身：
    // Authenticode 要求簽章憑證 CA=FALSE 且有 Code Signing EKU；
    // 拿 CA 直接簽會得到 0x80096019（TRUST_E_BASIC_CONSTRAINTS），驅動載不進去。
    private string SignPfxPath => Path.Combine(_artifactDir, "XinSpectSign.pfx");
    private string SignKeyPath => Path.Combine(_artifactDir, "XinSpectSign.pfxkey");

    private bool _caTrusted;
    private DriverServiceState _driverState = DriverServiceState.Unknown;
    private string _statusText = "狀態未知";
    private bool _driverConnected;

    public bool CaTrusted { get => _caTrusted; private set => SetProperty(ref _caTrusted, value); }
    public DriverServiceState DriverState { get => _driverState; private set => SetProperty(ref _driverState, value); }
    public bool IsEnabled => CaTrusted && DriverState == DriverServiceState.Running;

    /// <summary>驅動裝置握手是否實際通過（QUERY_INFO 對帳成功）——與「服務執行中」是兩件事，如實分開回報。</summary>
    public bool IsDriverConnected { get => _driverConnected; private set => SetProperty(ref _driverConnected, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>把動作結果的逐條說明附加在狀態文字後（給頁面顯示完整脈絡，不省略任何一步）。</summary>
    public void AppendStatusNotes(IReadOnlyList<string> notes)
    {
        if (notes.Count > 0) StatusText += "\n" + string.Join("\n", notes);
    }

    public DeepAccessStatus Enable()
    {
        if (!_isElevated)
        {
            var refused = new DeepAccessStatus(CaTrusted, DriverState, File.Exists(SysPath), IsEnabled,
                ["需要管理員權限才能啟用深層核心存取；未做任何變更。"]);
            StatusText = refused.Notes[0] + CurrentStateText();
            return refused;
        }

        var notes = new List<string>();
        X509Certificate2 ca = EnsureCa(notes);

        bool caTrusted = _trustStore.IsTrusted(ca.Thumbprint);
        if (caTrusted)
        {
            notes.Add($"CA 已在信任庫（thumbprint {ca.Thumbprint[..12]}…）。");
        }
        else
        {
            _trustStore.InstallTrusted(PublicOnly(ca));
            notes.Add("CA 已裝進 LocalMachine Root 與 TrustedPublisher（只放行這一張，不開全機 test-signing）。");
        }

        var driverState = _service.QueryState(ServiceName);
        bool sysPresent = File.Exists(SysPath);
        if (!sysPresent)
        {
            notes.Add($"未找到 {SysPath}：驅動未部署。請以管理員身分重新執行部署器（XinSpectDeploy），"
                      + "它會把 XsRegProbe.sys 放到這個路徑；手動建置見 XsRegProbe/BUILD-給使用者.md。");
        }
        else
        {
            // 部署器送來的是未簽章的 .sys；核心只載入「鏈到已信任根」的映像，
            // 所以先用本機這張 CA 簽一次（PFX 與密碼檔就是為此留的）。
            if (!IsSignedByCa(SysPath, ca) && File.Exists(SignPfxPath) && File.Exists(SignKeyPath))
            {
                try
                {
                    string pwd = File.ReadAllText(SignKeyPath).Trim();
                    notes.Add(_signer.Sign(SysPath, SignPfxPath, pwd, out var signMessage)
                        ? signMessage
                        : $"簽章失敗：{signMessage}（未簽章的驅動不會載入）");
                }
                catch (Exception ex)
                {
                    notes.Add($"簽章例外：{ex.Message}（未簽章的驅動不會載入）");
                }
            }

            try
            {
                switch (driverState)
                {
                    case DriverServiceState.NotFound:
                        _service.Install(ServiceName, SysPath);
                        _service.Start(ServiceName);
                        notes.Add($"已安裝並啟動服務 {ServiceName}（白名單唯讀，退出即卸載）。");
                        break;
                    case DriverServiceState.Stopped:
                        _service.Start(ServiceName);
                        notes.Add($"服務 {ServiceName} 已在停止狀態，僅啟動。");
                        break;
                    case DriverServiceState.Running:
                        notes.Add($"服務 {ServiceName} 已在執行。");
                        break;
                    default:
                        notes.Add($"服務 {ServiceName} 狀態查詢異常，未嘗試啟動。");
                        break;
                }
            }
            catch (Exception ex)
            {
                notes.Add($"服務 {ServiceName} 啟動失敗：{ex.Message}。請檢查核心簽章、testsigning 與系統事件記錄。");
            }
            if (_service.QueryState(ServiceName) == DriverServiceState.Running)
            {
                notes.Add(_handshakeProbe() is { } handshake
                    ? $"驅動{handshake}。"
                    : "服務已啟動，但裝置握手尚未通過（驅動可能載入失敗或簽章未過）——SPI/MMIO 仍三態。");
            }
            else
            {
                // 實際在 Windows 10/11 x64 上會走到這裡：核心模式程式碼完整性只接受
                // 鏈到 Microsoft 根的簽章，自簽憑證（即使已在 Root／TrustedPublisher）
                // 只對使用者模式有效，載入會回 577 ERROR_INVALID_IMAGE_HASH。
                notes.Add("驅動沒有載入：Windows 拒絕這個核心驅動的簽章"
                          + "（自簽憑證只在使用者模式有效，核心需 testsigning 或 Microsoft 簽章）。"
                          + "WinRing0 的 MSR／PCI 設定空間照常；它不提供可用的實體記憶體讀取，"
                          + "所以 MMIO（SPIBAR／MCHBAR／ECAM）會顯示讀不到。XsRegProbe 請視為開發機專用。");
            }
        }

        var status = RefreshStatus();
        var result = new DeepAccessStatus(_trustStore.IsTrusted(ca.Thumbprint), _service.QueryState(ServiceName),
            sysPresent, _trustStore.IsTrusted(ca.Thumbprint) && _service.QueryState(ServiceName) == DriverServiceState.Running, notes);
        // 顯示＝啟用過程的逐步說明＋「目前」狀態（RefreshStatus 已更新）；各說明只出現一次
        StatusText = string.Join("\n", notes) + CurrentStateText();
        return result;
    }

    public DeepAccessStatus Disable()
    {
        if (!_isElevated)
        {
            var refused = new DeepAccessStatus(CaTrusted, DriverState, File.Exists(SysPath), IsEnabled,
                ["需要管理員權限才能停用深層核心存取；未做任何變更。"]);
            StatusText = refused.Notes[0] + CurrentStateText();
            return refused;
        }

        var notes = new List<string>();
        if (_service.QueryState(ServiceName) != DriverServiceState.NotFound)
        {
            _service.StopAndDelete(ServiceName);
            notes.Add($"服務 {ServiceName} 已停止並刪除（驅動不留痕跡）。");
        }
        else
        {
            notes.Add($"服務 {ServiceName} 本就未安裝。");
        }

        if (TryLoadCa(out var ca))
        {
            if (_trustStore.IsTrusted(ca!.Thumbprint))
            {
                _trustStore.RemoveTrusted(ca.Thumbprint);
                notes.Add($"已移除本專案 CA 的信任（{ca.Thumbprint[..12]}…）；其他憑證不受影響。");
            }
            else
            {
                notes.Add("本專案 CA 原本就不在信任庫。");
            }
        }
        else
        {
            notes.Add($"找不到 {CaCerPath}，無法識別本專案 CA 的 thumbprint——信任庫未動（不掃庫誤刪）。");
        }

        var result = RefreshStatus();
        notes.AddRange(result.Notes);
        StatusText = string.Join("\n", notes) + CurrentStateText();
        return result with { Notes = notes };
    }

    public DeepAccessStatus RefreshStatus()
    {
        bool caTrusted = TryLoadCa(out var ca) && ca is { } c && _trustStore.IsTrusted(c.Thumbprint);
        var state = _service.QueryState(ServiceName);
        bool sysPresent = File.Exists(SysPath);
        bool connected = false;
        var notes = new List<string>();
        if (!caTrusted) notes.Add("CA 未信任");
        if (state != DriverServiceState.Running)
        {
            notes.Add($"驅動服務{state switch
            {
                DriverServiceState.NotFound => "未安裝",
                DriverServiceState.Stopped => "已停止",
                _ => "狀態未知",
            }}");
        }
        else
        {
            connected = _handshakeProbe() is { } handshake;
            notes.Add(connected ? "驅動已連線" : "服務執行中，但裝置握手失敗——MMIO 仍三態");
        }
        if (!sysPresent) notes.Add(".sys 未部署");

        CaTrusted = caTrusted;
        DriverState = state;
        IsDriverConnected = connected;
        // 狀態文字＝一句話判斷＋逐條原因（各出現一次——2026-10-03 修正重複顯示：StatusFrom 不再內嵌 notes）
        var status = new DeepAccessStatus(caTrusted, state, sysPresent, caTrusted && state == DriverServiceState.Running, notes);
        StatusText = StatusFrom(status) + (notes.Count > 0 ? "　" + string.Join("；", notes) : "");
        return status;
    }

    private string CurrentStateText() => $"\n目前：{StatusText}";

    private static string StatusFrom(DeepAccessStatus s) => s.IsEnabled
        ? "已啟用：CA 已信任、XsRegProbe 執行中。MMIO（SPIBAR／MCHBAR／ECAM）由這支白名單唯讀驅動提供；"
          + "MSR 與 PCI 設定空間照常走 WinRing0，兩者互不遮罩。"
        : "未完全啟用。";

    /// <summary>CA 不在就產生：自簽根（CA=TRUE、KeyCertSign）存 .cer（公）與 .pfx+密碼檔（私，供之後簽 .sys）。</summary>
    private X509Certificate2 EnsureCa(List<string> notes)
    {
        if (TryLoadCa(out var existing) && existing is { } ok)
        {
            EnsureLeafCertificate(existing, notes);
            return ok;
        }

        Directory.CreateDirectory(_artifactDir);
        using var rsa = RSA.Create(KeyBits);
        var request = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        File.WriteAllBytes(CaCerPath, ca.Export(X509ContentType.Cert));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(CaPfxPath, ca.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(CaKeyPath, password);

        // 葉憑證：拿來簽 .sys 的那一張（CA=FALSE + Code Signing EKU），由上面的 CA 簽出。
        using var leafKey = RSA.Create(KeyBits);
        var leafRequest = new CertificateRequest(
            SignSubject, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));   // Code Signing
        leafRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
        using var leaf = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1),
                                            DateTimeOffset.UtcNow.AddYears(3),
                                            RandomNumberGenerator.GetBytes(16));
        string signPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(SignPfxPath, leaf.CopyWithPrivateKey(leafKey)
            .Export(X509ContentType.Pkcs12, signPassword));
        File.WriteAllText(SignKeyPath, signPassword);

        // 私鑰材料（PFX＋密碼檔）限 SYSTEM／Administrators：取消繼承、只留這兩條規則
        RestrictToAdmins(CaPfxPath);
        RestrictToAdmins(CaKeyPath);
        RestrictToAdmins(SignPfxPath);
        RestrictToAdmins(SignKeyPath);
        notes.Add($"已產生自簽 CA {CaSubject}（{CaCerPath}）與簽章用葉憑證 {SignSubject}"
                  + "（PFX 與密碼檔同目錄且限管理員讀取，供 signtool 簽 .sys）。");
        return ca;
    }

    private void EnsureLeafCertificate(X509Certificate2 ca, List<string> notes)
    {
        if (File.Exists(SignPfxPath) && File.Exists(SignKeyPath)) return;
        if (!File.Exists(CaPfxPath) || !File.Exists(CaKeyPath))
        {
            notes.Add("既有 CA 缺少私鑰材料，無法補發驅動簽章葉憑證。");
            return;
        }

        string caPassword = File.ReadAllText(CaKeyPath).Trim();
        using var caWithKey = X509CertificateLoader.LoadPkcs12FromFile(CaPfxPath, caPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        using var leafKey = RSA.Create(KeyBits);
        var leafRequest = new CertificateRequest(
            SignSubject, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));
        leafRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
        using var leaf = leafRequest.Create(caWithKey, DateTimeOffset.UtcNow.AddDays(-1),
                                            DateTimeOffset.UtcNow.AddYears(3),
                                            RandomNumberGenerator.GetBytes(16));
        string signPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(SignPfxPath, leaf.CopyWithPrivateKey(leafKey)
            .Export(X509ContentType.Pkcs12, signPassword));
        File.WriteAllText(SignKeyPath, signPassword);
        RestrictToAdmins(SignPfxPath);
        RestrictToAdmins(SignKeyPath);
        notes.Add($"已補發簽章用葉憑證 {SignSubject}（既有 CA 保留不變）。");
    }

    /// <summary>把檔案 ACL 收緊為僅 SYSTEM 與 Administrators 可存取（取消繼承、清掉其他規則）。</summary>
    private static void RestrictToAdmins(string path)
    {
        try
        {
            var file = new FileInfo(path);
            FileSecurity security = file.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                new NTAccount("NT AUTHORITY", "SYSTEM"), FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new NTAccount("BUILTIN", "Administrators"), FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
        catch (PlatformNotSupportedException) { /* 非 Windows：不適用 */ }
    }

    private bool TryLoadCa(out X509Certificate2? ca)
    {
        ca = null;
        try
        {
            if (!File.Exists(CaCerPath)) return false;
            ca = X509CertificateLoader.LoadCertificateFromFile(CaCerPath);
            return string.Equals(ca.Subject, CaSubject, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>這個 .sys 是不是本機的簽章葉憑證簽的（是就不必重簽）。</summary>
    private static bool IsSignedByCa(string sysPath, X509Certificate2 ca)
    {
        try
        {
            // Authenticode 簽章只能從 PE 檔裡讀，這條 API 在 .NET 10 標成過時但沒有替代品
            // （X509CertificateLoader 只讀憑證檔，不解析 Authenticode）。
#pragma warning disable SYSLIB0057
            var signed = X509Certificate.CreateFromSignedFile(sysPath);
#pragma warning restore SYSLIB0057
            // 簽的是葉憑證（CN=XinSpect Driver Signing），由本機 CA 簽出：
            // 所以要比「發行者是不是本機 CA」＋「主體是不是我們的葉憑證」，
            // 不是拿葉憑證的指紋去比 CA 的指紋（那樣永遠不相等，會每次重簽）。
            return string.Equals(signed.Issuer, ca.Subject, StringComparison.OrdinalIgnoreCase)
                && string.Equals(signed.Subject, SignSubject, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // 未簽章／讀不到簽章：當成要簽
        }
    }

    private static X509Certificate2 PublicOnly(X509Certificate2 cert)
        => X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)); // 裝進信任庫一律用無私鑰的公憑證

    private static bool IsCurrentUserElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

/// <summary>真實信任庫：LocalMachine 的 Root 與 TrustedPublisher 兩個 store（裝/移都需要管理員）。邏輯由假件測試覆蓋。</summary>
public sealed class X509TrustStore : ICertTrustStore
{
    public bool IsTrusted(string thumbprint) =>
        Contains(StoreName.Root, thumbprint) || Contains(StoreName.TrustedPublisher, thumbprint);

    public void InstallTrusted(X509Certificate2 publicCertificate)
    {
        Add(StoreName.Root, publicCertificate);
        Add(StoreName.TrustedPublisher, publicCertificate);
    }

    public void RemoveTrusted(string thumbprint)
    {
        Remove(StoreName.Root, thumbprint);
        Remove(StoreName.TrustedPublisher, thumbprint);
    }

    private static bool Contains(StoreName name, string thumbprint)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
    }

    private static void Add(StoreName name, X509Certificate2 cert)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
    }

    private static void Remove(StoreName name, string thumbprint)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        foreach (var c in found) store.Remove(c);
    }
}

/// <summary>真實服務控制：advapi32 SCM（SERVICE_KERNEL_DRIVER、demand start）。邏輯由假件測試覆蓋。</summary>
public sealed class ScmDriverService : IDriverServiceControl
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceKernelDriver = 1;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceErrorNormal = 1;
    private const uint ServiceControlStop = 1;
    private const uint ServiceStopped = 0x1, ServiceStartPending = 0x2, ServiceRunning = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    public DriverServiceState QueryState(string serviceName)
    {
        IntPtr sc = OpenSCManagerW(null, null, ScManagerAllAccess);
        if (sc == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5 ? DriverServiceState.Unknown : DriverServiceState.NotFound;
        IntPtr svc = OpenServiceW(sc, serviceName, ServiceAllAccess);
        if (svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseServiceHandle(sc);
            return err == 1060 ? DriverServiceState.NotFound : DriverServiceState.Unknown; // 1060 = ERROR_SERVICE_DOES_NOT_EXIST
        }
        var status = new SERVICE_STATUS();
        bool ok = QueryServiceStatus(svc, ref status);
        CloseServiceHandle(svc);
        CloseServiceHandle(sc);
        if (!ok) return DriverServiceState.Unknown;
        return status.CurrentState switch
        {
            ServiceStopped => DriverServiceState.Stopped,
            ServiceRunning or ServiceStartPending => DriverServiceState.Running,
            _ => DriverServiceState.Unknown,
        };
    }

    public void Install(string serviceName, string sysPath)
    {
        IntPtr sc = Open(ScManagerAllAccess);
        try
        {
            IntPtr svc = CreateServiceW(sc, serviceName, serviceName, ServiceAllAccess, ServiceKernelDriver,
                ServiceDemandStart, ServiceErrorNormal, sysPath, null, IntPtr.Zero, null, null, null);
            if (svc == IntPtr.Zero) throw new InvalidOperationException($"CreateService({serviceName}) 失敗：Win32 錯誤 {Marshal.GetLastWin32Error()}");
            CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(sc); }
    }

    public void Start(string serviceName)
    {
        IntPtr svc = OpenService(ServiceAllAccess, serviceName);
        try
        {
            if (!StartServiceW(svc, 0, null))
                throw new InvalidOperationException($"StartService({serviceName}) 失敗：Win32 錯誤 {Marshal.GetLastWin32Error()}");
        }
        finally { CloseServiceHandle(svc); }
    }

    public void StopAndDelete(string serviceName)
    {
        IntPtr svc = OpenService(ServiceAllAccess, serviceName);
        try
        {
            var status = new SERVICE_STATUS();
            QueryServiceStatus(svc, ref status);
            if (status.CurrentState != ServiceStopped)
            {
                ControlService(svc, ServiceControlStop, ref status);
                // 核心驅動停止通常數秒內完成；這裡只發停機令，刪除以 DeleteService 的標記語義為準（重開機後必無）。
            }
            if (!DeleteService(svc))
                throw new InvalidOperationException($"DeleteService({serviceName}) 失敗：Win32 錯誤 {Marshal.GetLastWin32Error()}");
        }
        finally { CloseServiceHandle(svc); }
    }

    private IntPtr Open(uint access)
    {
        IntPtr sc = OpenSCManagerW(null, null, access);
        if (sc == IntPtr.Zero) throw new InvalidOperationException($"OpenSCManager 失敗：Win32 錯誤 {Marshal.GetLastWin32Error()}");
        return sc;
    }

    private IntPtr OpenService(uint access, string serviceName)
    {
        IntPtr sc = Open(access);
        IntPtr svc = OpenServiceW(sc, serviceName, access);
        CloseServiceHandle(sc);
        if (svc == IntPtr.Zero) throw new InvalidOperationException($"OpenService({serviceName}) 失敗：Win32 錯誤 {Marshal.GetLastWin32Error()}");
        return svc;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr scManager, string name, string displayName, uint access,
        uint serviceType, uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? accountName, string? password);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argc, string[]? argv);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint controlCode, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>
/// 真實簽章器：用 Windows SDK 的 signtool.exe 以本機 CA 的 PFX 簽 XsRegProbe.sys。
/// 為什麼一定要簽：核心只載入「鏈到已信任根、且有有效 Authenticode 簽章」的映像；
/// 部署器送來的是未簽章檔，不簽這一步，服務裝得起來但載不進去。
/// </summary>
public sealed class SigntoolSigner : ISysSigner
{
    public bool Sign(string sysPath, string pfxPath, string password, out string message)
    {
        try
        {
            string? signtool = FindSigntool();
            if (signtool is null)
            {
                message = "找不到 signtool.exe（需要 Windows SDK 的簽章工具）";
                return false;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(signtool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
                     {
                         "sign", "/fd", "sha256", "/f", pfxPath, "/p", password,
                         "/d", "XinSpect XsRegProbe", sysPath,
                     })
                psi.ArgumentList.Add(a);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                message = "signtool 無法啟動";
                return false;
            }
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            if (p.ExitCode != 0)
            {
                message = "signtool 回 " + p.ExitCode + "：" + FirstLine(output);
                return false;
            }
            message = "已用本機 CA 簽章 XsRegProbe.sys（signtool，/fd sha256）。";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        string line = (i < 0 ? s : s.Substring(0, i)).Trim();
        return line.Length > 160 ? line.Substring(0, 160) : line;
    }

    /// <summary>找 Windows SDK 的 signtool（版本目錄由高到低；x64 優先）。</summary>
    private static string? FindSigntool()
    {
        string kits = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "bin");
        if (!Directory.Exists(kits)) return null;
        foreach (string ver in Directory.GetDirectories(kits).OrderByDescending(d => d))
        {
            string x64 = Path.Combine(ver, "x64", "signtool.exe");
            if (File.Exists(x64)) return x64;
            string x86 = Path.Combine(ver, "x86", "signtool.exe");
            if (File.Exists(x86)) return x86;
        }
        return null;
    }
}
