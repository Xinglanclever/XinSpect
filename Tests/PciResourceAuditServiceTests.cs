using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public sealed class PciResourceAuditServiceTests
{
    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;

    [Fact]
    public void HardwareIds_解析完整識別欄位並採用第一個可靠ID()
    {
        var id = PciResourceAudit.ParseHardwareIds([
            "PCI\\VEN_ZZZZ&DEV_1234",
            "PCI\\VEN_10DE&DEV_2504&SUBSYS_40A11458&REV_A1",
            "PCI\\VEN_10DE&DEV_9999"]);

        Assert.NotNull(id);
        Assert.Equal(0x10DE, id!.VendorId);
        Assert.Equal(0x2504, id.DeviceId);
        Assert.Equal((uint?)0x40A11458U, id.SubsystemId);
        Assert.Equal((ushort?)0x1458, id.SubsystemVendorId);
        Assert.Equal((ushort?)0x40A1, id.SubsystemDeviceId);
        Assert.Equal((byte?)0xA1, id.RevisionId);
    }

    [Theory]
    [InlineData("USB\\VID_1234&PID_5678")]
    [InlineData("PCI\\VEN_123&DEV_5678")]
    [InlineData("PCI\\VEN_1234X&DEV_5678")]
    [InlineData("PCI\\VEN_1234&DEV_ZZZZ")]
    public void HardwareIds_不完整或非PCI時不猜(string raw)
        => Assert.Null(PciResourceAudit.ParseHardwareIds([raw]));

    [Fact]
    public void NativeBoundary_可注入且服務不觸碰WindowsAPI()
    {
        var fake = new FakeNative(Device("PCI\\VEN_1234&DEV_5678\\A"));
        var report = new PciResourceAuditService(fake).Audit();
        Assert.True(fake.Called);
        Assert.Single(report.Devices);
        Assert.Equal("1234", report.Devices[0].VendorId);
        Assert.Equal(PciResourceAudit.Unavailable, report.Devices[0].Device.InfName);
    }

    [Fact]
    public void 拓撲_只把本次列舉中的PCI父節點連起來()
    {
        var root = Device("PCI\\VEN_8086&DEV_1234\\ROOT");
        var child = Device("PCI\\VEN_10DE&DEV_2504\\GPU", root.InstanceId);
        var leaf = Device("PCI\\VEN_144D&DEV_A808\\NVME", child.InstanceId);
        var externalParent = Device("PCI\\VEN_1234&DEV_9999\\OTHER", "ACPI\\PNP0A08\\0");

        var report = PciResourceAudit.Analyze([leaf, externalParent, child, root]);
        var auditedRoot = report.Devices.Single(x => x.Device.InstanceId == root.InstanceId);
        var auditedChild = report.Devices.Single(x => x.Device.InstanceId == child.InstanceId);
        var auditedExternal = report.Devices.Single(x => x.Device.InstanceId == externalParent.InstanceId);

        Assert.Equal([child.InstanceId], auditedRoot.ChildPciInstanceIds);
        Assert.Equal(root.InstanceId, auditedChild.ParentPciInstanceId);
        Assert.Equal([leaf.InstanceId], auditedChild.ChildPciInstanceIds);
        Assert.Null(auditedExternal.ParentPciInstanceId);
    }

    [Fact]
    public void 問題碼與未啟動狀態_照Windows原碼產生客觀Finding()
    {
        var broken = Device("PCI\\VEN_1234&DEV_5678\\BAD", status: DnHasProblem, problem: 0x0C);
        var audit = Assert.Single(PciResourceAudit.Analyze([broken]).Devices);

        Assert.Contains(audit.Findings, f => f.Contains("problem code 12") && f.Contains("CM_PROB_NORMAL_CONFLICT"));
        Assert.Contains(audit.Findings, f => f.Contains("DN_STARTED"));
    }

    [Fact]
    public void 狀態有問題但問題碼不可用_不虛構問題碼()
    {
        var audit = Assert.Single(PciResourceAudit.Analyze([
            Device("PCI\\VEN_1234&DEV_5678\\BAD", status: DnStarted | DnHasProblem, problem: null)
        ]).Devices);
        Assert.Contains(audit.Findings, f => f.Contains("DN_HAS_PROBLEM") && f.Contains("problem code was unavailable"));
        Assert.DoesNotContain(audit.Findings, f => f.Contains("problem code 0"));
    }

    [Fact]
    public void 記憶體與IO重疊_雙方都報告而IRQ共享不武斷判衝突()
    {
        var a = Device("PCI\\VEN_1111&DEV_0001\\A", resources:
        [
            Resource(PciResourceKind.Memory, 0x1000, 0x1FFF),
            Resource(PciResourceKind.Irq, 32, 32),
        ]);
        var b = Device("PCI\\VEN_2222&DEV_0002\\B", resources:
        [
            Resource(PciResourceKind.Memory, 0x1800, 0x2FFF),
            Resource(PciResourceKind.Irq, 32, 32),
        ]);

        var report = PciResourceAudit.Analyze([a, b]);
        Assert.All(report.Devices, d => Assert.Contains(d.Findings,
            f => f.Contains("Memory") && f.Contains("0x1800-0x1FFF") && f.Contains("may be intentional")));
        Assert.All(report.Devices, d => Assert.DoesNotContain(d.Findings, f => f.Contains("IRQ range")));
    }

    [Fact]
    public void 父橋接視窗包含子裝置BAR不算資源衝突()
    {
        var bridge = Device("PCI\\VEN_8086&DEV_1234\\ROOT", resources:
        [
            Resource(PciResourceKind.Memory, 0xD0000000, 0xDFFFFFFF),
        ]);
        var gpu = Device("PCI\\VEN_10DE&DEV_2504\\GPU", bridge.InstanceId, resources:
        [
            Resource(PciResourceKind.Memory, 0xD1000000, 0xD1FFFFFF),
        ]);

        Assert.All(PciResourceAudit.Analyze([bridge, gpu]).Devices, d => Assert.Empty(d.Findings));
    }

    [Fact]
    public void 祖先橋接視窗包含孫裝置BAR也不算資源衝突()
    {
        var root = Device("PCI\\VEN_8086&DEV_1000\\ROOT", resources:
        [
            Resource(PciResourceKind.Memory, 0xD0000000, 0xDFFFFFFF),
        ]);
        var bridge = Device("PCI\\VEN_8086&DEV_2000\\BRIDGE", root.InstanceId);
        var nvme = Device("PCI\\VEN_144D&DEV_A808\\NVME", bridge.InstanceId, resources:
        [
            Resource(PciResourceKind.Memory, 0xD1000000, 0xD1003FFF),
        ]);

        Assert.All(PciResourceAudit.Analyze([root, bridge, nvme]).Devices, d => Assert.Empty(d.Findings));
    }

    [Fact]
    public void 相鄰範圍不算重疊_同裝置內別名範圍也不自我衝突()
    {
        var a = Device("PCI\\VEN_1111&DEV_0001\\A", resources:
        [
            Resource(PciResourceKind.IoPort, 0x100, 0x1FF),
            Resource(PciResourceKind.IoPort, 0x180, 0x1A0),
        ]);
        var b = Device("PCI\\VEN_2222&DEV_0002\\B", resources:
        [
            Resource(PciResourceKind.IoPort, 0x200, 0x2FF),
        ]);
        Assert.All(PciResourceAudit.Analyze([a, b]).Devices, d => Assert.Empty(d.Findings));
    }

    [Fact]
    public void 資源長度包含起點終點且記憶體明示不是VRAM()
    {
        var resource = Resource(PciResourceKind.Memory, 0x1000, 0x1FFF);
        Assert.Equal(0x1000UL, resource.Length);
        Assert.Contains("Windows 配置資源", resource.Label);
        Assert.Contains("不是 VRAM", resource.Label);
        Assert.Contains("ALLOC_LOG_CONF", resource.Source);
    }

    [Fact]
    public void 空列舉_明示資料不可用而不是宣稱沒有PCI裝置()
    {
        string finding = Assert.Single(PciResourceAudit.Analyze([]).Findings);
        Assert.Contains("unavailable", finding);
        Assert.Contains("not proof", finding);
    }

    private static PciAssignedResource Resource(PciResourceKind kind, ulong start, ulong end)
        => new(kind, start, end, "cfgmgr32 ALLOC_LOG_CONF (Windows configured resource)");

    private static PciDeviceSnapshot Device(string id, string? parent = null,
        uint? status = DnStarted, uint? problem = 0, IReadOnlyList<PciAssignedResource>? resources = null)
        => new()
        {
            InstanceId = id,
            Description = "test device",
            DeviceClass = "System",
            HardwareIds = [id.Split('\\')[0] + "\\" + id.Split('\\')[1]],
            ParentInstanceId = parent ?? PciResourceAudit.Unavailable,
            DevNodeStatus = status,
            ProblemCode = problem,
            Resources = resources ?? [],
        };

    private sealed class FakeNative(params PciDeviceSnapshot[] devices) : IPciResourceNative
    {
        public bool Called { get; private set; }
        public IReadOnlyList<PciDeviceSnapshot> EnumeratePresentPciDevices()
        {
            Called = true;
            return devices;
        }
    }
}
