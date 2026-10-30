using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「同一次收集只列舉一次」的裝飾器（第 324 輪 D12）與系統碟代號解析（D7）。
/// </summary>
public class AcpiOnceSourceTests
{
    private sealed class CountingSource(params byte[][] tables) : IAcpiTableSource
    {
        public int Calls { get; private set; }
        public bool Available => true;
        public string? UnavailableReason => null;

        public IReadOnlyList<byte[]> ReadAll()
        {
            Calls++;
            return tables.ToList();
        }
    }

    private static byte[] Table(string sig)
    {
        var b = new byte[40];
        System.Text.Encoding.ASCII.GetBytes(sig).CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), 40u);
        return b;
    }

    [Fact]
    public void 多次呼叫只穿透一次()
    {
        var inner = new CountingSource(Table("FACP"), Table("APIC"));
        var once = new OnceAcpiTableSource(inner);

        Assert.Equal(2, once.ReadAll().Count);
        Assert.Equal(2, once.ReadAll().Count);
        Assert.Equal(2, once.ReadAll().Count);

        Assert.Equal(1, inner.Calls);   // 真正的斷言：底層只被問一次
        Assert.Equal(1, once.ReadCount);
    }

    [Fact]
    public void 沒人問就不要列舉()
    {
        var inner = new CountingSource();
        _ = new OnceAcpiTableSource(inner);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public void 空清單也是有效結果_不會重試()
    {
        var inner = new CountingSource();
        var once = new OnceAcpiTableSource(inner);
        Assert.Empty(once.ReadAll());
        Assert.Empty(once.ReadAll());
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void 可用性與理由要穿透()
    {
        var inner = new CountingSource(Table("FACP"));
        var once = new OnceAcpiTableSource(inner);
        Assert.True(once.Available);
        Assert.Null(once.UnavailableReason);
    }

    // ── D7：系統碟不是寫死的 C: ─────────────────────────────────────────

    [Fact]
    public void 系統碟代號由SystemDirectory推出()
    {
        string? letter = SystemVolumeProbe.SystemDriveLetter();
        Assert.NotNull(letter);
        Assert.Equal(2, letter!.Length);
        Assert.True(char.IsAsciiLetterUpper(letter[0]));
        Assert.Equal(':', letter[1]);
        // 與行程實際所在一致——這台的 Windows 目錄就在這個代號上
        Assert.StartsWith(letter, Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 裝置路徑用同一組代號_且開機磁區沿用同一份推導()
    {
        string? path = SystemVolumeProbe.SystemVolumeDevicePath();
        Assert.NotNull(path);
        Assert.Equal(@"\\.\" + SystemVolumeProbe.SystemDriveLetter(), path);
        // 兩個服務必須指向同一個裝置——各自寫一份推導，遲早一個改了一個沒改
        Assert.Equal(SystemVolumeProbe.SystemVolume, SystemBootSectorProbe.SystemVolume);
    }

    [Fact]
    public void 代號文字不是空白且有冒號()
    {
        string text = SystemVolumeProbe.SystemDriveText();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.EndsWith(":", text);
    }
}
