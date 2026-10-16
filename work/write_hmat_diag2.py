# -*- coding: utf-8 -*-
"""診斷：TailValues 的邊界檢查為何讓延遲值回 0。

從規格與測試兩邊的位元組數推算：合成表長 = 32 + listBytes + 16，
TailValues 需要的條件是 tailStart + 12 <= len，也就是 32 + listBytes + 12 <= 32 + listBytes + 16
—— 應該成立。所以問題可能在 off：測試裡子結構的 off 是 40（表頭 36 + 保留 4），
而 len 被 WriteHeader 寫成 b.Length。若 Structures 回傳的 len 與 b.Length 不同，就會不吻合。

直接把兩邊的值印出來比對。
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

public class HmatDiag2
{
    [Fact]
    public void 診斷_印出邊界檢查的中間值()
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

        lines.Add($"合成子結構長度 b.Length = {b.Length}");
        lines.Add($"寫進 length 欄位的值 = {BitConverter.ToUInt32(b, 4)}");

        var body = new List<byte>();
        body.AddRange(new byte[4]);
        body.AddRange(b);
        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "HMAT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        body.CopyTo(table, AcpiTable.HeaderLength);

        lines.Add($"整表長度 = {table.Length}");
        var s = AcpiHmat.Structures(table);
        foreach (var (type, len, off) in s)
        {
            lines.Add($"結構：type={type} len={len} off={off}");
            lines.Add($"  邊界：tailStart(32+{2 * 4}) + 12 = {32 + 2 * 4 + 12} vs len={len} → "
                + (32 + 2 * 4 + 12 <= len ? "過" : "不過"));
            lines.Add($"  表邊界：off+tailStart+12 = {off + 32 + 2 * 4 + 12} vs table.Length={table.Length} → "
                + (off + 32 + 2 * 4 + 12 <= table.Length ? "過" : "不過"));
            int t = off + 32 + 2 * 4 + 4;
            lines.Add($"  讀取位移 t={t}，值={BitConverter.ToUInt16(table, t)}");
        }

        var lat = AcpiHmat.Latencies(table);
        foreach (var l in lat)
            lines.Add($"解出：hierarchy={l.Hierarchy} read={l.ReadLatencyNs} write={l.WriteLatencyNs}");

        System.IO.File.WriteAllLines(System.IO.Path.Combine(FindRoot(), "work", "hmat_diag2.txt"), lines);
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
io.open('Tests/HmatDiag2.cs', 'w', encoding='utf-8', newline='').write(diag)
print('已寫入 Tests/HmatDiag2.cs')
