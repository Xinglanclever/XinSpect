using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WinRing0 的來源必須<b>跟著執行檔走</b>。
/// </summary>
/// <remarks>
/// <para>
/// 這條守門的由來：LHM 0.9.6 起移除了 Ring0／WinRing0，本專案因此以隔離 ALC 載入 0.9.4 取得
/// 驅動；但載入來源一度只讀 NuGet 全域快取（兩條候選路徑之一還寫死成特定使用者名稱）。
/// 開發機的 NuGet 快取剛好有那個套件所以看起來一切正常，單檔發佈到別的機器上就整層讀不到——
/// 實測把快取移走，backend.msr／backend.mmio 直接變成 NotSupported，而驅動就緒卡宣稱的來源
/// 正是「內嵌的 0.9.4」。所以這裡釘住三件事：資源真的在組件裡、它是真的 PE 映像、
/// 而且它真的載得出 Ring0 與七個方法。
/// </para>
/// <para>
/// 刻意<b>不呼叫 Ring0.Open()</b>：那會真的安裝並啟動核心驅動，測試套件不該有這種副作用
/// （與 <see cref="WinRing0BridgeTests"/> 同一條慣例）。
/// </para>
/// </remarks>
public class EmbeddedLhm094Tests
{
    /// <summary>與 <c>XinSpect.csproj</c> 的 EmbedLhm094 目標、<c>WinRing0Bridge.Embedded094</c> 同一個字串。</summary>
    private const string LogicalName = "XinSpect.embedded.LibreHardwareMonitorLib-0.9.4.dll";

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static Stream Open() =>
        typeof(WinRing0Bridge).Assembly.GetManifestResourceStream(LogicalName)!;

    /// <summary>把內嵌的組件載進獨立 ALC；相依交給預設 ALC（0.9.6 的相依已在行程裡）。</summary>
    private static Assembly LoadEmbedded(Stream stream)
    {
        var alc = new AssemblyLoadContext("XinSpect-Test-LHM094", isCollectible: false);
        alc.Resolving += (c, name) =>
        {
            try { return AssemblyLoadContext.Default.LoadFromAssemblyName(name); }
            catch { return null; }
        };
        return alc.LoadFromStream(stream);
    }

    [Fact]
    public void 內嵌資源存在且是PE映像()
    {
        using var s = Open();
        Assert.NotNull(s);
        Assert.True(s.Length > 100_000, $"內嵌的 0.9.4 只有 {s.Length} 位元組，不像真的組件");
        Assert.Equal(0x4D, s.ReadByte());   // MZ
        Assert.Equal(0x5A, s.ReadByte());
    }

    [Fact]
    public void 內嵌的LHM094載得出Ring0的四個必要與三個選用方法()
    {
        using var s = Open();
        var ty = LoadEmbedded(s).GetType("LibreHardwareMonitor.Hardware.Ring0");
        Assert.NotNull(ty);

        // 四個必要方法（少一個，WinRing0Bridge 就整個建不起來）
        Assert.NotNull(ty!.GetMethod("Open", All, Type.EmptyTypes));
        Assert.NotNull(ty.GetMethod("Close", All, Type.EmptyTypes));
        Assert.NotNull(ty.GetMethod("ReadMsr", All,
            new[] { typeof(uint), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() }));
        Assert.NotNull(ty.GetMethod("WriteMsr", All, new[] { typeof(uint), typeof(uint), typeof(uint) }));

        // 三個選用能力的來源也在（少了它們，PCIe 鏈路／SPD 直讀／MMIO 會靜默降級成讀不到）
        Assert.NotNull(ty.GetMethod("ReadPciConfig", All,
            new[] { typeof(uint), typeof(uint), typeof(uint).MakeByRefType() }));
        Assert.NotNull(ty.GetMethod("GetPciAddress", All, new[] { typeof(byte), typeof(byte), typeof(byte) }));
        Assert.NotNull(ty.GetMethod("WritePciConfig", All,
            new[] { typeof(uint), typeof(uint), typeof(uint) }));
        var readIo = ty.GetMethod("ReadIoPort", All, new[] { typeof(uint) });
        Assert.NotNull(readIo);
        Assert.Equal(typeof(byte), readIo!.ReturnType);   // 形狀不符時 WinRing0Bridge 會當作沒有
        Assert.NotNull(ty.GetMethod("WriteIoPort", All, new[] { typeof(uint), typeof(byte) }));

        // ReadMemory 是泛型（ReadMemory<T>(UInt64, ref T[])），精確型別查不到——比照 WinRing0Bridge
        // 的形狀比對：第二參數是 ByRef 且元素是陣列。這條就是 MMIO 的地基。
        var readMem = ty.GetMethods(All).FirstOrDefault(m => m.Name == "ReadMemory"
            && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 2
            && m.GetParameters()[0].ParameterType == typeof(ulong)
            && m.GetParameters()[1].ParameterType.IsByRef
            && m.GetParameters()[1].ParameterType.GetElementType() is { IsArray: true });
        Assert.NotNull(readMem);
    }

    [Fact]
    public void 內嵌版本帶著WinRing0驅動資源()
    {
        // Ring0 以內嵌資源帶著 gzip 過的 WinRing0x64／WinRing0（x86）；沒有它們就載不成驅動。
        using var s = Open();
        var names = LoadEmbedded(s).GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith("WinRing0x64.gz", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith("WinRing0.gz", StringComparison.OrdinalIgnoreCase));
    }
}
