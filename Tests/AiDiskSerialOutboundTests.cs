using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

// Real storage-tool registration and agent HTTP serialization, synthetic data only.
// No MainViewModel construction, WMI, device, process, or external network.
[Collection(WpfCollection.Name)]
public sealed class AiDiskSerialOutboundTests
{
    private const string OtherSerial = "SN-SECOND-AUDIT-78245";

    private sealed class AgentHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            string reply = Bodies.Count switch
            {
                1 => """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_disk_audit","type":"function","function":{"name":"get_disk_health","arguments":"{}"}}]}}]}""",
                2 => """{"choices":[{"message":{"content":"synthetic complete"}}]}""",
                _ => throw new InvalidOperationException("Unexpected extra HTTP request"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData("SN-AUDIT-98371", true, true)]
    [InlineData("SN-AUDIT-98371", false, true)]
    [InlineData("SN-AUDIT-98371", true, false)]
    [InlineData("SN-AUDIT-98371", false, false)]
    [InlineData("Q", true, true)]
    [InlineData("SN / private tail", true, true)]
    [InlineData("SN・quoted\"\\path\nprivate-tail", true, true)]
    [InlineData("私密序號甲乙", true, true)]
    public async Task StorageToolSerialIsOpaqueAtActualHttpBoundary(string serial, bool master, bool tools)
    {
        var physical = new[]
        {
            new PhysicalDiskInfo { Model="AuditModel", SizeBytes=1_000_000_000_000L, Kind=DiskKind.NvmeSsd,
                InterfaceType="NVMe", Partitions=2, SerialNumber=serial, HealthDetail="synthetic-health" },
            new PhysicalDiskInfo { Model="AuditSecond", SerialNumber=OtherSerial },
        };
        var live = new StorageRow("AuditLive") { TempC=42, CapacityText="1 TB", InterfaceType="NVMe", Firmware="AUDIT-FW" };
        var volume = new VolumeInfo("Q:", @"Q:\") { Label="AuditVolume", TypeText="NTFS", TotalBytes=100_000_000_000, FreeBytes=50_000_000_000 };
        int reads=0;
        var box = new AiToolbox();
        AiToolboxBuilder.AddStorage(box, () => { reads++; return new[] { live }; },
            () => { reads++; return physical; }, () => { reads++; return new[] { volume }; },
            () => { reads++; return "synthetic-summary"; });
        Assert.Equal(0, reads);
        string raw = box.Invoke("get_disk_health", "{}");
        Assert.Equal(4, reads);
        string token = JsonSerializer.Serialize(serial);
        Assert.Contains("・序號：" + token, raw);
        Assert.Equal(serial, JsonSerializer.Deserialize<string>(token));
        reads=0;

        var settings = new SettingsService(loadFromDisk:false)
        {
            AiProvider=(int)AiProvider.OpenAiCompatible, AiBaseUrl="http://127.0.0.1:1/v1", AiModel="test-model",
            AiApiKey="", AiStreaming=false, AiKeepHistory=false, AiAgentMode=true,
            AiAgentNoticeShown=true, AiAgentPreviewToolResults=false, AiSystemPrompt="synthetic system",
            AiHistoryTurns=0, AiRedactIdentity=master, AiAgentRedactToolResults=tools,
        };
        string folder=Path.Combine(Path.GetTempPath(), "XinSpectDiskSerial_"+Guid.NewGuid().ToString("N"));
        var handler=new AgentHandler();
        var ai=new AiService(settings,folder) { HandlerOverride=handler, Tools=box };
        try
        {
            await ai.SendAsync("Read synthetic storage");
            Assert.Equal(2,handler.Bodies.Count);
            Assert.Equal(4,reads);
            using var first=JsonDocument.Parse(handler.Bodies[0]);
            Assert.Equal("get_disk_health",Assert.Single(first.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("function").GetProperty("name").GetString());
            Assert.DoesNotContain(first.RootElement.GetProperty("messages").EnumerateArray(), m=>m.GetProperty("role").GetString()=="tool");
            using var second=JsonDocument.Parse(handler.Bodies[1]);
            var messages=second.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var tool=Assert.Single(messages,m=>m.GetProperty("role").GetString()=="tool");
            Assert.Equal("get_disk_health",tool.GetProperty("name").GetString());
            Assert.Equal("call_disk_audit",tool.GetProperty("tool_call_id").GetString());
            var assistant=Assert.Single(messages,m=>m.TryGetProperty("tool_calls",out _));
            Assert.Equal("call_disk_audit",Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray()).GetProperty("id").GetString());
            string sent=tool.GetProperty("content").GetString()!;
            bool redact=master && tools;
            string expected=redact
                ? raw.Replace("・序號："+token,"・序號：（已遮蔽：序號）",StringComparison.Ordinal)
                     .Replace("・序號："+JsonSerializer.Serialize(OtherSerial),"・序號：（已遮蔽：序號）",StringComparison.Ordinal)
                : raw;
            Assert.Equal(expected,sent);
            foreach(string preserved in new[]{"AuditModel","1 TB","NVMe SSD","NVMe","2 個分割","synthetic-health","42 °C","AUDIT-FW","AuditVolume","NTFS","synthetic-summary"})
                Assert.Contains(preserved,sent);
            Assert.Equal(redact?2:0,ai.LastRedactionCount);
            var row=Assert.Single(ai.Messages,m=>m.IsTool);
            Assert.EndsWith("送出內容："+sent,row.Text);
            Assert.Contains(redact?"已遮蔽 2 處":"未遮蔽",row.Text);
            Assert.Equal(serial,physical[0].SerialNumber);
            Assert.Equal(OtherSerial,physical[1].SerialNumber);
            Assert.Equal("synthetic complete",ai.Messages.Last(m=>!m.IsUser && !m.IsTool).Text);
        }
        finally
        {
            ai.HandlerOverride=null;
            if(Directory.Exists(folder)) Directory.Delete(folder,recursive:true);
        }
    }

    [Fact]
    public void MachineSerialIsRemovedBeforeOtherIdentityRulesAndKindIsUnique()
    {
        string serial="01234567-89ab-cdef-0123-456789abcdef / 00-1A-2B-3C-4D-5E";
        string input="・序號："+JsonSerializer.Serialize(serial)+"・S.M.A.R.T. 42 °C\nSerial Number: SN-OTHER-89123";
        var result=AiRedactor.Apply(input);
        Assert.Equal("・序號：（已遮蔽：序號）・S.M.A.R.T. 42 °C\nSerial Number: （已遮蔽：序號）",result.Text);
        Assert.Equal(2,result.Count);
        Assert.Equal(new[]{"序號"},result.Kinds);
    }
}
