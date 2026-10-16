# -*- coding: utf-8 -*-
"""診斷 HMAT 延遲階層：印出解碼器實際看到的長度與位移。

延遲值讀成 0，但頻寬那條已經過了——差別在 Latencies 與 Bandwidths 之間。
最可能在於合成表的 length 欄位：WriteHeader 寫的是 b.Length，
而延遲表改成 +16 之後長度也跟著變了，理應沒問題。
直接把中間值印出來看。
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

public class HmatDiag
{
    [Fact]
    public void 診斷_印出延遲階層的內部狀態()
    {
        var lines = new List<string>();
        int listBytes = 2 * 4;                  // 1 initiator + 1 target
        var b = new byte[32 + listBytes + 16];
        BitConverter.GetBytes((ushort)1).CopyTo(b, 0);
        BitConverter.GetBytes((uint)b.Length).CopyTo(b, 4);
        b[8] = 0;
        BitConverter.GetBytes(1u).CopyTo(b, 12);
        BitConverter.GetBytes(1u).CopyTo(b, 16);
        BitConverter.GetBytes((uint)3).CopyTo(b, 24);
        int t0 = 32 + listBytes;
        BitConverter.GetBytes(1u).CopyTo(b, t0);
        BitConverter.GetBytes((ushort)120).CopyTo(b, t0 + 4);
        BitConverter.GetBytes((ushort)140).CopyTo(b, t0 + 6);

        lines.Add($"子結構長度 = {b.Length}，尾端起點 = {t0}");

        // 組整張表
        var body = new List<byte>();
        body.AddRange(new byte[4]);
        body.AddRange(b);
        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "HMAT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        body.CopyTo(table, AcpiTable.HeaderLength);

        var s = AcpiHmat.Structures(table);
        lines.Add($"走訪到 {s.Count} 個子結構");
        foreach (var (type, len, off) in s)
            lines.Add($"  type={type} len={len} off={off}");

        var lat = AcpiHmat.Latencies(table);
        lines.Add($"解出 {lat.Count} 筆延遲");
        foreach (var l in lat)
            lines.Add($"  hierarchy={l.Hierarchy} read={l.ReadLatencyNs} write={l.WriteLatencyNs}");

        System.IO.File.WriteAllLines(System.IO.Path.Combine(FindRoot(), "work", "hmat_diag.txt"), lines);
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
io.open('Tests/HmatDiag.cs', 'w', encoding='utf-8', newline='').write(diag)
print('已寫入 Tests/HmatDiag.cs')
