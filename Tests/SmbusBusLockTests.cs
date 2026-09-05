using System.Threading;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SMBus 的軟體層仲裁鎖。
/// </summary>
/// <remarks>
/// 硬體的 INUSE_STS 旗號防的是 BIOS／SMM（那不理 Windows 的任何鎖），這把具名互斥鎖防的是
/// 同機的其他監控軟體——CPU-Z、AIDA64、HWiNFO、主機板燈光軟體都遵守同一個名字。
/// 兩層都要，因為它們防的是不同的東西。
/// <para>
/// 測試一律用 <c>Local\</c> 命名空間下的隨機名字：真正的名字是全機共用的，
/// 在測試裡搶它會真的去卡住使用者正在跑的 CPU-Z。
/// </para>
/// </remarks>
public class SmbusBusLockTests
{
    private static string TempName() => @"Local\xinspect-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void 取得之後釋放_下一個人才拿得到()
    {
        string name = TempName();

        var first = SmbusBusLock.TryAcquire(name, 200, out var note);
        Assert.NotNull(first);
        Assert.Equal("", note);
        first!.Dispose();

        using var second = SmbusBusLock.TryAcquire(name, 200, out _);
        Assert.NotNull(second);
    }

    [Fact]
    public void 別的執行緒持有時取不到_並且說得出可能是誰在用()
    {
        string name = TempName();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            using var mine = SmbusBusLock.TryAcquire(name, 1000, out _);
            held.Set();
            release.Wait(5000);
        }) { IsBackground = true };
        holder.Start();
        Assert.True(held.Wait(5000));

        var blocked = SmbusBusLock.TryAcquire(name, 100, out var note);
        Assert.Null(blocked);
        Assert.Contains("CPU-Z", note);

        release.Set();
        holder.Join(5000);
    }

    /// <summary>
    /// 持有者當掉沒釋放時，Windows 會在下一個等待者身上丟 AbandonedMutexException。
    /// 那時候鎖其實是我們的了，但匯流排狀態未知——必須取得並把這件事標出來，
    /// 不能靜靜當成一次乾淨的取得。
    /// </summary>
    [Fact]
    public void 前一個持有者沒釋放就結束時仍然取得_但要標明()
    {
        string name = TempName();
        using var held = new ManualResetEventSlim();

        // 自己也持有一個名稱句柄，否則持有者執行緒結束後核心物件可能被回收，
        // 「被遺棄」這個狀態就跟著消失，測到的會是一次乾淨的取得。
        using var keepAlive = new Mutex(false, name);

        var abandoner = new Thread(() =>
        {
            SmbusBusLock.TryAcquire(name, 1000, out _);   // 故意不 Dispose，執行緒直接結束
            held.Set();
        }) { IsBackground = true };
        abandoner.Start();
        Assert.True(held.Wait(5000));
        abandoner.Join(5000);

        using var got = SmbusBusLock.TryAcquire(name, 500, out var note);
        Assert.NotNull(got);
        Assert.Contains("未正常釋放", note);
    }

    [Fact]
    public void 重複Dispose是安全的()
    {
        var lock1 = SmbusBusLock.TryAcquire(TempName(), 200, out _);
        Assert.NotNull(lock1);
        lock1!.Dispose();
        lock1.Dispose();
    }

    [Fact]
    public void 正式名稱就是各家監控軟體共用的那一個()
        => Assert.Equal(@"Global\Access_SMBUS.HTP.Method", SmbusBusLock.WellKnownName);
}
