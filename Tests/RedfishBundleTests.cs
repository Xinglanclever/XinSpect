using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>離線 Redfish：只讀本機目錄、不連網；壞資料與路徑穿越只記警告，不補 0。</summary>
public class RedfishBundleTests
{
    private static void Write(string root, string relative, string json)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static string FullBundle(string root)
    {
        Write(root, "redfish/v1/index.json", """
        {"@odata.id":"/redfish/v1","GeneratedAtUtc":"2026-11-03T01:00:00Z",
         "Managers":{"@odata.id":"/redfish/v1/Managers"},
         "Chassis":{"@odata.id":"/redfish/v1/Chassis"},
         "Systems":{"@odata.id":"/redfish/v1/Systems"}}
        """);
        Write(root, "redfish/v1/Managers/index.json", """
        {"Members":[{"@odata.id":"/redfish/v1/Managers/1"}]}
        """);
        Write(root, "redfish/v1/Managers/1/index.json", """
        {"Id":"1","Model":"BMC-X","FirmwareVersion":"1.2.3","ManagerType":"BMC",
         "Status":{"State":"Enabled","Health":"OK"}}
        """);
        Write(root, "redfish/v1/Chassis/index.json", """
        {"Members":[{"@odata.id":"/redfish/v1/Chassis/1"}]}
        """);
        Write(root, "redfish/v1/Chassis/1/index.json", """
        {"Id":"1","ChassisType":"RackMount","SerialNumber":"CH-1","PowerState":"On",
         "PhysicalSecurity":{"IntrusionSensor":"Normal"},
         "Thermal":{"@odata.id":"/redfish/v1/Chassis/1/Thermal"},
         "Power":{"@odata.id":"/redfish/v1/Chassis/1/Power"}}
        """);
        Write(root, "redfish/v1/Chassis/1/Thermal/index.json", """
        {"Temperatures":[{"Name":"Inlet","ReadingCelsius":24,"UpperCritical":45,"Status":{"Health":"OK"}}],
         "Fans":[{"Name":"Fan1","Reading":8000,"Status":{"Health":"OK"}}]}
        """);
        Write(root, "redfish/v1/Chassis/1/Power/index.json", """
        {"PowerSupplies":[{"Name":"PSU1","Model":"PWR-800","FirmwareVersion":"2.0",
          "PowerOutputWatts":450,"LineInputVoltage":230,"Status":{"Health":"OK"}}],
         "PowerControl":[{"PowerConsumedWatts":320}]}
        """);
        Write(root, "redfish/v1/Systems/index.json", """
        {"Members":[{"@odata.id":"/redfish/v1/Systems/1"}]}
        """);
        Write(root, "redfish/v1/Systems/1/index.json", """
        {"Id":"1","Manufacturer":"ACME","Model":"R1","SerialNumber":"SYS-1","BiosVersion":"9.9","PowerState":"On"}
        """);
        return root;
    }

    [Fact]
    public void 完整離線包_輸出BMC機箱電源風扇溫度且退出碼0()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-redfish-" + Guid.NewGuid().ToString("N"));
        try
        {
            FullBundle(dir);
            var stdout = new StringWriter();
            int exit = CliService.Run(["--redfish", dir], () => [], stdout, new StringWriter());

            Assert.Equal(CliService.ExitOk, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            var root = doc.RootElement;
            Assert.Equal("redfish", root.GetProperty("scope").GetString());
            Assert.Equal("BMC-X", root.GetProperty("managers")[0].GetProperty("model").GetString());
            Assert.Equal("1.2.3", root.GetProperty("managers")[0].GetProperty("firmwareVersion").GetString());
            Assert.Equal("Normal", root.GetProperty("chassis")[0].GetProperty("intrusion").GetString());
            Assert.Equal("RackMount", root.GetProperty("chassis")[0].GetProperty("type").GetString());
            Assert.Equal(450, root.GetProperty("powerSupplies")[0].GetProperty("outputWatts").GetDouble());
            Assert.Equal(8000, root.GetProperty("fans")[0].GetProperty("readingRpm").GetDouble());
            Assert.Equal(24, root.GetProperty("temperatures")[0].GetProperty("readingCelsius").GetDouble());
            Assert.Equal(320, root.GetProperty("powerConsumedWatts").GetDouble());
            Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
            Assert.Contains("不連網", root.GetProperty("note").GetString()!, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 空目錄_退出碼2且如實說找不到ServiceRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-redfish-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stdout = new StringWriter();
            int exit = CliService.Run(["--redfish", dir], () => [], stdout, new StringWriter());

            Assert.Equal(CliService.ExitPartial, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(0, doc.RootElement.GetProperty("counts").GetProperty("managers").GetInt32());
            Assert.Contains(doc.RootElement.GetProperty("warnings").EnumerateArray(),
                w => w.GetString()!.Contains("ServiceRoot", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 外部URL與路徑穿越_只記警告不讀目錄外()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-redfish-bad-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetDirectoryName(dir)!, "SECRET-redfish.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(outside, "{\"Secret\":\"SECRET-VALUE\"}");
        try
        {
            Write(dir, "redfish/v1/index.json", """
            {"@odata.id":"/redfish/v1",
             "Managers":{"@odata.id":"https://bmc.example/redfish/v1/Managers"},
             "Chassis":{"@odata.id":"../../SECRET-redfish"}}
            """);
            var stdout = new StringWriter();
            int exit = CliService.Run(["--redfish", dir], () => [], stdout, new StringWriter());

            Assert.Equal(CliService.ExitPartial, exit);
            string text = stdout.ToString();
            Assert.DoesNotContain("SECRET-VALUE", text, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(text);
            var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray()
                .Select(w => w.GetString()!).ToList();
            Assert.Contains(warnings, w => w.Contains("外部 URL", StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.Contains("路徑穿越", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }
}