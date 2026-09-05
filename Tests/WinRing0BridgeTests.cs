using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WinRing0 橋接的<b>降級契約</b>。
/// </summary>
/// <remarks>
/// <para>
/// 這組測試刻意只驗「橋接失敗時會怎樣」，而不驗成功路徑：成功路徑會呼叫
/// <c>Ring0.Open()</c>，那會真的把核心驅動安裝並啟動起來。測試套件不該有這種副作用，
/// 所以整個專案的慣例是——特權存取層保持極薄，驗證力量放在純函式解碼器上。
/// </para>
/// <para>
/// 但降級契約必須釘住：SMBus／SPD 那條路徑一旦讀不到，全站顯示的是「讀不到（原因）」。
/// 如果 <see cref="WinRing0Bridge.IoPortAvailable"/> 在橋接失敗時回 true，
/// 呼叫端就會以為自己拿到了真值，然後把 0 或 0xFF 當成資料解讀出去——
/// 那正是驗機功能最不能犯的錯（假結論比沒有結論糟得多）。
/// </para>
/// </remarks>
public class WinRing0BridgeTests
{
    /// <summary>
    /// 失敗的橋接內部沒有反射方法，所有存取都必須在進到硬體之前就短路。
    /// 這裡故意呼叫寫入方法：它必須回 false 而<b>不能真的送出任何 out 指令</b>——
    /// 之所以敢在測試裡呼叫，正是因為斷言的內容就是「它短路了」。
    /// </summary>
    [Fact]
    public void 失敗的橋接不得聲稱支援IO埠或PCI()
    {
        using var bridge = WinRing0Bridge.CreateFailed("測試用的失敗橋接");

        Assert.False(bridge.Available);
        Assert.Equal("測試用的失敗橋接", bridge.Error);
        Assert.False(bridge.PciAvailable);
        Assert.False(bridge.IoPortAvailable);

        Assert.Null(bridge.ReadIoPortByte(0));
        Assert.False(bridge.WriteIoPortByte(0, 0));
        Assert.Null(bridge.ReadPciConfig(0, 0, 0, 0));
        Assert.Null(bridge.ReadMsrPair64(0));
    }

    /// <summary>Dispose 失敗的橋接不得拋例外，也不得動到共用的引用計數。</summary>
    [Fact]
    public void 重複Dispose失敗的橋接是安全的()
    {
        var bridge = WinRing0Bridge.CreateFailed("x");
        bridge.Dispose();
        bridge.Dispose();
    }
}
