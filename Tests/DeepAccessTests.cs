using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace XinSpect.Tests;

public sealed class DeepAccessTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-deepaccess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string DeploySys(string dir)
    {
        string path = Path.Combine(dir, "XsRegProbe.sys");
        File.WriteAllText(path, "fake driver payload");
        return path;
    }

    [Fact]
    public void 啟用_全新環境_產生CA裝信任並載驅動()
    {
        string dir = TempDir();
        DeploySys(dir);
        var store = new FakeStore();
        var service = new FakeService();
        try
        {
            var sut = new DeepAccessService(store, service, dir, isElevated: true);

            var status = sut.Enable();

            Assert.True(status.CaTrusted);
            Assert.Equal(DriverServiceState.Running, status.DriverState);
            Assert.True(status.IsEnabled);
            Assert.Equal(1, service.InstallCount); // 重複啟用不重複裝（見下一測）
            Assert.True(File.Exists(Path.Combine(dir, "XinSpectCA.cer")));
            Assert.Contains(store.Trusted, t => t.Length >= 40); // 有 thumbprint 進信任庫

            // CA 本體要真的是自簽根（CA=TRUE），否則後面簽 .sys 這條窄路就是假的
            using var ca = new X509Certificate2(Path.Combine(dir, "XinSpectCA.cer"));
            var constraints = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
            Assert.True(constraints.CertificateAuthority);
            Assert.Equal(DeepAccessService.CaSubject, ca.Subject);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 啟用後PFX與密碼檔限管理員存取()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = TempDir();
        DeploySys(dir);
        var sut = new DeepAccessService(new FakeStore(), new FakeService(), dir, isElevated: true);
        try
        {
            sut.Enable();

            foreach (string name in new[] { "XinSpectCA.pfx", "XinSpectCA.pfxkey" })
            {
                string path = Path.Combine(dir, name);
                Assert.True(File.Exists(path), $"{name} 不存在");
                var rules = new FileInfo(path).GetAccessControl()
                    .GetAccessRules(true, false, typeof(System.Security.Principal.NTAccount));
                Assert.True(rules.Count > 0);
                Assert.All(rules.Cast<System.Security.AccessControl.FileSystemAccessRule>(), rule =>
                    Assert.True(rule.IdentityReference.Value.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)
                                || rule.IdentityReference.Value.Contains("Administrators", StringComparison.OrdinalIgnoreCase),
                                $"出現非預期的存取主體：{rule.IdentityReference.Value}"));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 啟用_sys缺席_CA信任但如實標驅動未載不謊稱全開()
    {
        string dir = TempDir();
        var sut = new DeepAccessService(new FakeStore(), new FakeService(), dir, isElevated: true);

        var status = sut.Enable();

        Assert.True(status.CaTrusted);
        Assert.Equal(DriverServiceState.NotFound, status.DriverState);
        Assert.False(status.IsEnabled);
        Assert.Contains(status.Notes, n => n.Contains("XsRegProbe.sys"));
    }

    [Fact]
    public void 非提權_拒做且不動任何狀態()
    {
        string dir = TempDir();
        DeploySys(dir);
        var store = new FakeStore();
        var service = new FakeService();
        try
        {
            var sut = new DeepAccessService(store, service, dir, isElevated: false);

            var status = sut.Enable();

            Assert.False(status.IsEnabled);
            Assert.Empty(store.Trusted);
            Assert.Equal(DriverServiceState.NotFound, service.QueryState(DeepAccessService.ServiceName));
            Assert.Equal(0, service.InstallCount);
            Assert.Contains(status.Notes, n => n.Contains("管理員"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 停用_停刪服務並只移除自己CA的信任()
    {
        string dir = TempDir();
        DeploySys(dir);
        var store = new FakeStore();
        var service = new FakeService();
        try
        {
            var sut = new DeepAccessService(store, service, dir, isElevated: true);
            sut.Enable();
            store.Trusted.Add("AAAA_BBBB_別人的根"); // 別人的根，停用後必須還在

            var status = sut.Disable();

            Assert.Equal(DriverServiceState.NotFound, status.DriverState);
            Assert.False(status.IsEnabled);
            Assert.Contains(status.Notes, n => n.Contains("已停止並刪除"));
            Assert.Contains(status.Notes, n => n.Contains("已移除"));
            Assert.Contains("AAAA_BBBB_別人的根", store.Trusted); // 只動自己那張
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 停用_CA檔遺失_如實標無法移信任不誤刪()
    {
        string dir = TempDir();
        var store = new FakeStore();
        var sut = new DeepAccessService(store, new FakeService(), dir, isElevated: true);
        store.Trusted.Add("CCCC_某張在庫裡的憑證");

        var status = sut.Disable();

        Assert.Contains(status.Notes, n => n.Contains("無法識別本專案 CA"));
        Assert.Contains("CCCC_某張在庫裡的憑證", store.Trusted); // 沒有憑證指紋就什麼都不刪
    }

    [Fact]
    public void 重複啟用_不重複安裝服務()
    {
        string dir = TempDir();
        DeploySys(dir);
        var service = new FakeService();
        try
        {
            var sut = new DeepAccessService(new FakeStore(), service, dir, isElevated: true);
            sut.Enable();
            sut.Enable();
            Assert.Equal(1, service.InstallCount);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 狀態查詢_未啟用時如實報告各環節()
    {
        string dir = TempDir();
        var sut = new DeepAccessService(new FakeStore(), new FakeService(), dir, isElevated: false);

        var status = sut.RefreshStatus();

        Assert.False(status.CaTrusted);
        Assert.Equal(DriverServiceState.NotFound, status.DriverState);
        Assert.False(status.SysPresent);
        Assert.False(status.IsEnabled);
        Assert.Contains(status.Notes, n => n.Contains("CA 未信任"));
    }

    [Fact]
    public void 啟用後握手通過_如實回報已連線()
    {
        string dir = TempDir();
        DeploySys(dir);
        var sut = new DeepAccessService(new FakeStore(), new FakeService(), dir, isElevated: true,
            handshakeProbe: () => "裝置握手通過（能力協商與允許清單對帳成功）");
        try
        {
            var status = sut.Enable();
            Assert.Contains(status.Notes, n => n.Contains("握手通過"));
            Assert.True(sut.IsDriverConnected);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 服務執行中但握手失敗_兩者如實分開回報()
    {
        string dir = TempDir();
        DeploySys(dir);
        var sut = new DeepAccessService(new FakeStore(), new FakeService(), dir, isElevated: true,
            handshakeProbe: () => null);
        try
        {
            var status = sut.Enable();
            // 服務在跑（IsEnabled 真），但裝置握手沒過——不可謊稱可用
            Assert.Contains(status.Notes, n => n.Contains("握手尚未通過"));
            Assert.False(sut.IsDriverConnected);
            Assert.True(sut.IsEnabled);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private sealed class FakeStore : ICertTrustStore
    {
        public HashSet<string> Trusted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsTrusted(string thumbprint) => Trusted.Contains(thumbprint);
        public void InstallTrusted(X509Certificate2 publicCertificate) => Trusted.Add(publicCertificate.Thumbprint);
        public void RemoveTrusted(string thumbprint) => Trusted.Remove(thumbprint);
    }

    private sealed class FakeService : IDriverServiceControl
    {
        public DriverServiceState State { get; private set; } = DriverServiceState.NotFound;
        public int InstallCount { get; private set; }
        public DriverServiceState QueryState(string serviceName) => State;
        public void Install(string serviceName, string sysPath) { InstallCount++; State = DriverServiceState.Stopped; }
        public void Start(string serviceName) => State = DriverServiceState.Running;
        public void StopAndDelete(string serviceName) => State = DriverServiceState.NotFound;
    }
}
