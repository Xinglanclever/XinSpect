using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TCPA 表事實的守門（U10-AC-0042）。
/// 守的是：存在性與表頭欄位（client／server 兩種佈局）、沒有表＝NotApplicable、
/// 短表拒解、以及「未成文的欄位不硬解」的界線（server 表只展開到已對照的欄位）。
/// </summary>
public class AcpiTcpaFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>造一張 ACPI 表：簽章＋長度＋指定內容（形狀照 AcpiTpm2WpbtFactsTests）。</summary>
    private static byte[] Table(string signature, int totalLength, Action<byte[]>? fill = null)
    {
        var t = new byte[totalLength];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(t, 0);
        BitConverter.GetBytes((uint)totalLength).CopyTo(t, 4);
        fill?.Invoke(t);
        return t;
    }

    /// <summary>client 佈局的 TCPA 表：platform_class＝0、最小長度 u32@0x26、位址 u64@0x2A。</summary>
    private static byte[] TcpaClient(uint logLength, ulong logAddress)
        => Table("TCPA", 50, t =>
        {
            t[36] = 0; t[37] = 0;
            BitConverter.GetBytes(logLength).CopyTo(t, 0x26);
            BitConverter.GetBytes(logAddress).CopyTo(t, 0x2A);
        });

    /// <summary>server 佈局的 TCPA 表：platform_class＝1、最小長度 u64@0x28、位址 u64@0x30。</summary>
    private static byte[] TcpaServer(ulong logLength, ulong logAddress)
        => Table("TCPA", 100, t =>
        {
            t[36] = 1; t[37] = 0;
            BitConverter.GetBytes(logLength).CopyTo(t, 0x28);
            BitConverter.GetBytes(logAddress).CopyTo(t, 0x30);
        });

    private static Dictionary<string, byte[]> Tables(params byte[][] tables)
        => tables.ToDictionary(
            t => System.Text.Encoding.ASCII.GetString(t, 0, 4),
            t => t, StringComparer.Ordinal);

    // ── 沒有表 ──────────────────────────────────────────────────────────

    [Fact]
    public void 沒有表來源時兩把鍵都標不支援()
    {
        var facts = AcpiTcpaFactsService.Collect(At, null);

        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.Equal(2, facts.Count);
    }

    [Fact]
    public void 沒有TCPA表時要說那與沒有TPM是不同的斷言()
    {
        var facts = AcpiTcpaFactsService.Collect(At, Tables(Table("TPM2", 0x3C)));
        var t = facts.Single(f => f.Key == AcpiTcpaFactsService.TcpaKey);

        Assert.Equal(FactAvailability.NotApplicable, t.Availability);
        Assert.Null(t.NumericValue);
        Assert.Contains("「韌體沒有留下 TPM 1.2 時代的信任線索表」與「沒有 TPM」是不同的斷言", t.UnavailableReason);
    }

    // ── 有表 ────────────────────────────────────────────────────────────

    [Fact]
    public void client佈局的表頭欄位要如實陳述()
    {
        var t = AcpiTcpaFactsService.Collect(At, Tables(TcpaClient(0x1000, 0x7FAB0000)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaKey);

        Assert.Equal(FactAvailability.Present, t.Availability);
        Assert.Contains("platform class＝client（0）", t.Value);
        Assert.Contains("事件日誌最小長度 4096 bytes", t.Value);
        Assert.Contains("事件日誌位址 0x7FAB0000", t.Value);
        Assert.Equal(4096.0, t.NumericValue);
    }

    [Fact]
    public void server佈局的表頭欄位要照server偏移陳述()
    {
        var t = AcpiTcpaFactsService.Collect(At, Tables(TcpaServer(0x2000, 0x1000)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaKey);

        Assert.Equal(FactAvailability.Present, t.Availability);
        Assert.Contains("platform class＝server（1）", t.Value);
        Assert.Contains("事件日誌最小長度 8192 bytes", t.Value);
        Assert.Contains("事件日誌位址 0x1000", t.Value);
        Assert.Contains("未成文，不展開", t.Value);
    }

    [Fact]
    public void 服務檔要寫明未成文欄位不硬解的界線()
    {
        var f = AcpiTcpaFactsService.Collect(At, Tables(TcpaServer(1, 2)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaKey);

        // server 表只展開到已對照的欄位，其餘如實說「未成文」——出處守門的一部分
        Assert.Contains("未成文", f.Value);
        Assert.Contains("本機查不到原文", f.Source);
    }

    [Fact]
    public void 短於表頭時要如實拒解()
    {
        var t = AcpiTcpaFactsService.Collect(At, Tables(Table("TCPA", 36)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaKey);

        Assert.Equal(FactAvailability.ReadError, t.Availability);
        Assert.Contains("如實拒解，不猜", t.UnavailableReason);
    }

    [Fact]
    public void client表短於佈局所需時也要拒解()
    {
        var t = AcpiTcpaFactsService.Collect(At, Tables(Table("TCPA", 42, fill: b => { b[36] = 0; })))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaKey);

        Assert.Equal(FactAvailability.ReadError, t.Availability);
        Assert.Contains("client", t.UnavailableReason);
    }

    // ── 與 TPM2 表的並存對照 ────────────────────────────────────────────

    [Fact]
    public void 兩表並存時要說兩代線索並存且不仲裁()
    {
        var f = AcpiTcpaFactsService.Collect(At,
                Tables(TcpaClient(1, 2), Table("TPM2", 0x3C)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaCrossKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("兩代 TPM 信任線索表並存", f.Value);
        Assert.Contains("不互斥、也不仲裁", f.Value);
    }

    [Fact]
    public void 只有TCPA而無TPM2時要說兩代線索只存其一()
    {
        var f = AcpiTcpaFactsService.Collect(At, Tables(TcpaClient(1, 2)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaCrossKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0.0, f.NumericValue);
        Assert.Contains("兩代線索只存其一", f.Value);
        Assert.Contains("應看 tpm.* 與 sb.tpmdev", f.Value);
    }

    [Fact]
    public void 沒有TCPA表時對照鍵要標不適用並說出TPM2的狀態()
    {
        var onlyTpm2 = AcpiTcpaFactsService.Collect(At, Tables(Table("TPM2", 0x3C)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaCrossKey);
        var neither = AcpiTcpaFactsService.Collect(At, Tables())
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaCrossKey);

        Assert.Equal(FactAvailability.NotApplicable, onlyTpm2.Availability);
        Assert.Contains("只有 TPM2 是現代平台的正常樣貌", onlyTpm2.UnavailableReason);
        Assert.Equal(FactAvailability.NotApplicable, neither.Availability);
        Assert.Contains("兩代線索都沒有留下", neither.UnavailableReason);
    }

    /// <summary>界線：這張表不判斷 TPM 的存在與可用。</summary>
    [Fact]
    public void 對照鍵要陳述自己不判斷TPM存在性()
    {
        var f = AcpiTcpaFactsService.Collect(At,
                Tables(TcpaClient(1, 2), Table("TPM2", 0x3C)))
            .Single(f => f.Key == AcpiTcpaFactsService.TcpaCrossKey);

        Assert.Contains("不判斷 TPM 的存在與可用", f.Value);
    }
}
