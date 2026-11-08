using System.Runtime.InteropServices;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// patch-02（§3.3-4 補）：Overlapped64 佈局釘死測試——x64 上 hEvent 必須落在 offset 24、
/// 結構總大小 32。歷史病灶：手寫 <c>Marshal.WriteIntPtr(ov, IntPtr.Size, evt)</c> 把 event
/// 寫在 offset 8（Slot 2），NVMe 健康資料因此整批錯位（審計 P1-2/P1-3）。
/// 佈局錯了不是「測試紅燈」而是「磁碟健康判決全錯」——所以這條要測試釘死，不靠 review。
/// </summary>
public class OverlappedLayoutPinTests
{
    [Fact]
    public void Overlapped64的hEvent必須落在offset24且結構總大小32()
    {
        // StorageSmartService.Overlapped64 是 private——佈局契約以反射逐一驗證，
        // 欄位順序或明確 offset 一改動，這裡立刻紅燈。
        var t = typeof(XinSpect.StorageSmartService).GetNestedType("Overlapped64",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(t is not null, "StorageSmartService.Overlapped64 不存在——佈局契約被移走了？");

        var fields = t!.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                 | System.Reflection.BindingFlags.Instance);
        // 〔修復 v3.21〕他們的版是 5 欄（Internal/InternalHigh/Offset/OffsetHigh/hEvent 明列）——
        // 比我 v3.20 的 4 欄（Offset 合併宣告）更貼近 winbase.h 原始結構；欄數斷言跟隨。
        Assert.Equal(5, fields.Length);

        foreach (var f in fields)
        {
                        object? o = Marshal.OffsetOf(t, f.Name);
            Assert.True(o is not null, f.Name + "：OffsetOf 回 null——欄位不在版面內");
            var offset = (long)(nint)o;
            if (f.Name == "hEvent")
            {
                Assert.True(offset == 24,
                    $"Overlapped64.hEvent 在 offset {offset}，x64 佈局必須是 24——" +
                    "手寫 overlapped 的 event 落錯欄，IOCP/CancelIoEx 判決整組失效（審計 P1-2）");
            }
        }
        Assert.Equal(32, Marshal.SizeOf(t));
    }
}
