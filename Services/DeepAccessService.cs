using System.IO;
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

    public DeepAccessService(ICertTrustStore? trustStore = null, IDriverServiceControl? serviceControl = null,
        string? artifactDir = null, bool? isElevated = null, Func<string?>? handshakeProbe = null)
    {
        _trustStore = trustStore ?? new X509TrustStore();
        _service = serviceControl ?? new ScmDriverService();
        _artifactDir = artifactDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XinSpect", "Driver");
        _isElevated = isElevated ?? IsCurrentUserElevated();
        _handshakeProbe = handshakeProbe ?? DefaultHandshakeProbe;
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
            notes.Add($"未找到 {SysPath}：驅動未安裝。請先依 XsRegProbe/BUILD-給使用者.md 編譯並複製 .sys。");
        }
        else
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
            if (_service.QueryState(ServiceName) == DriverServiceState.Running)
            {
                notes.Add(_handshakeProbe() is { } handshake
                    ? $"驅動{handshake}。"
                    : "服務已啟動，但裝置握手尚未通過（驅動可能載入失敗或簽章未過）——SPI/MMIO 仍三態。");
            }
        }

        var status = RefreshStatus();
        if (status.Notes.Count > 0) notes.AddRange(status.Notes);
        var result = new DeepAccessStatus(_trustStore.IsTrusted(ca.Thumbprint), _service.QueryState(ServiceName),
            sysPresent, _trustStore.IsTrusted(ca.Thumbprint) && _service.QueryState(ServiceName) == DriverServiceState.Running, notes);
        StatusText = StatusFrom(result);
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
        if (result.Notes.Count > 0) notes.AddRange(result.Notes);
        StatusText = StatusFrom(result) + "　" + string.Join(" ", notes);
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
        StatusText = StatusFrom(new DeepAccessStatus(caTrusted, state, sysPresent, caTrusted && state == DriverServiceState.Running, notes));
        return new DeepAccessStatus(caTrusted, state, sysPresent, caTrusted && state == DriverServiceState.Running, notes);
    }

    private string CurrentStateText() => $"\n目前：{StatusText}";

    private static string StatusFrom(DeepAccessStatus s) => s.IsEnabled
        ? "已啟用：CA 已信任、驅動執行中。SPI/MMIO 事實將可讀取（僅限允許清單內範圍）。"
        : $"未完全啟用（{(s.Notes.Count > 0 ? string.Join("；", s.Notes) : "原因不明，請再查詢")}）。";

    /// <summary>CA 不在就產生：自簽根（CA=TRUE、KeyCertSign）存 .cer（公）與 .pfx+密碼檔（私，供之後簽 .sys）。</summary>
    private X509Certificate2 EnsureCa(List<string> notes)
    {
        if (TryLoadCa(out var existing) && existing is { } ok) return ok;

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
        // 私鑰材料（PFX＋密碼檔）限 SYSTEM／Administrators：取消繼承、只留這兩條規則
        RestrictToAdmins(CaPfxPath);
        RestrictToAdmins(CaKeyPath);
        notes.Add($"已產生自簽 CA {CaSubject}（{CaCerPath}；PFX 與密碼檔同目錄且限管理員讀取，供 signtool 簽 .sys）。");
        return ca;
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
            ca = new X509Certificate2(CaCerPath);
            return string.Equals(ca.Subject, CaSubject, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static X509Certificate2 PublicOnly(X509Certificate2 cert)
        => new(cert.Export(X509ContentType.Cert)); // 裝進信任庫一律用無私鑰的公憑證

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
