# -*- coding: utf-8 -*-
"""診斷：直接呼叫 TailValues 看它到底回什麼。

上一輪的診斷只驗了「我手算的位移」，沒有驗解碼器自己的計算結果。
這一輪直接把 TailValues 的邏輯抄一份到測試裡，兩邊並排比對。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')

diag = '''using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public class HmatDiag3
{
    [Fact]
    public void 診斷_並排比對解碼器與手算()
    {
        var lines = new List<string>();

        int listBytes = 2 * 4;
        var b = new byte[32 + listBytes + 16];
        BitConverter.GetBytes((ushort)1).CopyTo(b, 0);
        BitConverter.GetBytes((uint)b.Length).CopyTo(b, 4);
        BitConverter.GetBytes(1u).CopyTo(b, 12);
        BitConverter.GetBytes(1u).CopyTo(b, 16);
        BitConverter.GetBytes((uint)3).CopyTo(b, 24);
        int t0 = 32 + listBytes;
        BitConverter.GetBytes(1u).CopyTo(b, t0);
        BitConverter.GetBytes((ushort)120).CopyTo(b, t0 + 4);
        BitConverter.GetBytes((ushort)140).CopyTo(b, t0 + 6);

        var body = new List<byte>();
        body.AddRange(new byte[4]);
        body.AddRange(b);
        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "HMAT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        body.CopyTo(table, AcpiTable.HeaderLength);

        var s = AcpiHmat.Structures(table).Single();
        int off = s.Offset, len = (int)s.Length;

        // ── 解碼器實際看到的兩個清單數量 ──
        uint init = BitConverter.ToUInt32(table.AsSpan(off + 12));
        uint tgt = BitConverter.ToUInt32(table.AsSpan(off + 16));
        lines.Add($"解碼器讀到 initiators={init} targets={tgt}（預期 1 與 1）");
        lines.Add($"off={off} len={len}");
        lines.Add($"tailStart = 32 + {init}*4 + {tgt}*4 = {32 + init * 4 + tgt * 4}");
        lines.Add($"檢查：tailStart+12 = {32 + init * 4 + tgt * 4 + 12} vs len {len}");
        lines.Add($"檢查：off+tailStart+12 = {off + 32 + init * 4 + tgt * 4 + 12} vs table {table.Length}");

        int t = off + (int)(32 + init * 4 + tgt * 4) + 4;
        lines.Add($"解碼器會用的位移 t = {t}");
        if (t + 8 <= table.Length)
            lines.Add($"t..t+8 的值 = {BitConverter.ToUInt16(table, t)}, {BitConverter.ToUInt16(table, t + 2)}, "
                + $"{BitConverter.ToUInt16(table, t + 4)}, {BitConverter.ToUInt16(table, t + 6)}");

        // ── 解碼器實際回傳 ──
        var lat = AcpiHmat.Latencies(table);
        foreach (var l in lat)
            lines.Add($"Latencies：hierarchy={l.Hierarchy} read={l.ReadLatencyNs} write={l.WriteLatencyNs}");

        var bw = AcpiHmat.Bandwidths(table);
        foreach (var x in bw)
            lines.Add($"Bandwidths：hierarchy={x.Hierarchy} read={x.ReadBandwidth} write={x.WriteBandwidth} "
                + $"rns={x.ReadLatencyNs} wns={x.WriteLatencyNs}");

        System.IO.File.WriteAllLines(System.IO.Path.Combine(FindRoot(), "work", "hmat_diag3.txt"), lines);
        Assert.True(true);
    }

    private static string FindRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir!.FullName;
    }
}
'''
io.open('Tests/HmatDiag3.cs', 'w', encoding='utf-8', newline='').write(diag)
print('已寫入 Tests/HmatDiag3.cs')
