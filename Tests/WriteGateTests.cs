using System;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 寫入閘門（v2.51，主綱 §5.3 最小版）：每一次硬體寫入都要進帳，帳本要能被申报面讀到。
/// </summary>
public class WriteGateTests
{
    /// <summary>橋接的三個寫入方法各自必須接 WriteGate.Record——這是「底層收口」的源碼級正對照：
    /// 哪天有人把 Record 行刪掉或新寫一個繞過閘門的寫入方法，這條紅。</summary>
    [Fact]
    public void 橋接的每個寫入方法都要記帳()
    {
        string bridge = System.IO.File.ReadAllText(RepoFile("Services", "WinRing0Bridge.cs"));
        foreach (string method in new[] { "WriteMsrPair", "WritePciConfig", "WriteIoPortByte" })
        {
            int start = bridge.IndexOf($"public bool {method}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{method} 找不到了——命名改了要同步更新這條守門");
            int end = bridge.IndexOf("\n    }", start, StringComparison.Ordinal);
            Assert.True(end > start, $"{method} 方法尾找不到");
            string body = bridge[start..end];
            Assert.Contains("WriteGate.Record", body, StringComparison.Ordinal);
        }
    }

    /// <summary>Services/ 裡不得有第二組直接寫 Ring0 的原生入口（只有橋接自己與測試用的 fake 例外）。</summary>
    [Fact]
    public void 橋接之外不得有人直接呼叫Ring0寫入()
    {
        // 白名單式掃描：這三個 Ring0 反射方法名只准出現在 WinRing0Bridge 的委派表裡。
        var ring0Writes = new[] { "Ring0.WriteMsr", "Ring0.WritePciConfig", "Ring0.WriteIoPortByte",
                                 "WriteMsr.Invoke", "WritePciConfig.Invoke", "WriteIoPort.Invoke" };
        // 只准出現在橋接與閘門自己的檔裡；其他 Services 檔案要寫硬體，只能經橋接的公開方法（它們負責記帳）
        foreach (string file in System.IO.Directory.EnumerateFiles(RepoRoot() + "/Services", "*.cs",
                     System.IO.SearchOption.AllDirectories))
        {
            string name = System.IO.Path.GetFileName(file);
            if (name == "WinRing0Bridge.cs" || name == "WriteGate.cs") continue;
            string text = System.IO.File.ReadAllText(file);
            foreach (var api in ring0Writes)
                Assert.False(text.Contains(api, StringComparison.Ordinal),
                    $"{name} 直接呼叫 {api}——硬體寫入必須经橋接（它負責記帳），不能繞過閘門。");
        }
    }

    [Fact]
    public void 帳本記錄成功與失敗且描述如實()
    {
        WriteGate.Reset();
        Assert.Equal("本次執行沒有對硬體的寫入（唯讀收集）。", WriteGate.DescribeSession());

        WriteGate.Record("MSR 0x309", "PmuProgrammingService", "EAX=0x00000001 EDX=0x00000000", true);
        WriteGate.Record("I/O 0x70", "CmosService", "out byte 0xFF", false);
        var session = WriteGate.Session;
        Assert.Equal(2, session.Count);
        Assert.True(session[0].Succeeded);
        Assert.False(session[1].Succeeded);

        string text = WriteGate.DescribeSession();
        Assert.Contains("2 筆", text);
        Assert.Contains("成功", text);
        Assert.Contains("失敗", text);
        Assert.Contains("MSR 0x309", text);

        WriteGate.Reset();
        Assert.Empty(WriteGate.Session);   // 新輪開始不帶舊帳——舊帳不冒充現況
    }

    [Fact]
    public void 帳本有上限且截斷如實說明()
    {
        WriteGate.Reset();
        for (int i = 0; i < WriteGate.Cap + 100; i++)
            WriteGate.Record($"MSR 0x{i:X}", "VolumeTest", "x", true);
        Assert.Equal(WriteGate.Cap, WriteGate.Session.Count);
        Assert.Contains("已達帳本上限", WriteGate.DescribeSession());
        WriteGate.Reset();
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        return dir?.FullName ?? throw new System.IO.DirectoryNotFoundException();
    }

    private static string RepoFile(params string[] parts)
        => System.IO.Path.Combine(RepoRoot(), System.IO.Path.Combine(parts));
}
