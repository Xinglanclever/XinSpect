using System.IO;

namespace XinSpect;

/// <summary>
/// 終審修復的探針：XAML 接線與 native 委派簽名是建構期事實，行為測試看不見，
/// 由這兩個方法把事實告訴測試（比對原始碼字串，不執行 native 呼叫）。
/// 找原始碼的路徑以 RepoRoot 為準（測試跑在 bin 目錄下）。
/// </summary>
public static class DeepBenchViewProbe
{
    /// <summary>DeepBenchView 根節點是否把 DataContext 指到 DeepBench 子模型；沒有的話整頁繫結全空。</summary>
    public static bool BindsToDeepBenchContext() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Views", "DeepBenchView.xaml"))
            .Contains("DataContext=\"{Binding DeepBench}\"", StringComparison.Ordinal);

    internal static string RepoRoot()
    {
        // 測試組件在 Tests/bin/... 下跑；往上找 csproj 所在目錄
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}

public static class PresentFramePacingNativeProbe
{
    /// <summary>
    /// Present pacing engine 用裸 vtable 呼叫 ID3D11DeviceContext：ClearRenderTargetView 是第 50 格
    /// （d3d11.h 權威序：CopyResource=47、UpdateSubresource=48、CopyStructureCount=49、ClearRenderTargetView=50）。
    /// 第 267 輪曾誤釘為 49——那是 CopyStructureCount；2026-11-07 更正注記回復為 50，
    /// 並反向釘死 49 不得出現（它會以 (rtv, color) 形狀呼叫三參數的 CopyStructureCount）。
    /// </summary>
    public static bool UsesClearRenderTargetViewSlot()
    {
        string source = File.ReadAllText(Path.Combine(DeepBenchViewProbe.RepoRoot(), "Services", "DeepBench", "Interop", "D3D11PresentFramePacingEngine.cs"));
        return source.Contains("ContextSlotClearRenderTargetView = 50", StringComparison.Ordinal)
            && source.Contains("ContextSlotOMSetRenderTargets = 33", StringComparison.Ordinal)
            && source.Contains("VTable(context, ContextSlotClearRenderTargetView)", StringComparison.Ordinal)
            && !source.Contains("VTable(context, 49)", StringComparison.Ordinal)
            && !source.Contains("VTable(context, 50)", StringComparison.Ordinal);
    }
}

public static class D3D11NativeProbe
{
    /// <summary>ID3D11DeviceContext::CSSetShader（vtable slot 69）實際上是 4 參數；簽名寫成 6 參數會把 shader 指標傳進 NumClassInstances 造成 native crash。</summary>
    public static bool HasCorrectCssSetShaderSignature()
    {
        string source = File.ReadAllText(Path.Combine(DeepBenchViewProbe.RepoRoot(), "Services", "DeepBench", "Interop", "D3D11Native.cs"));
        return source.Contains("GetVTableSlot(context, 69)", StringComparison.Ordinal) &&
               source.Contains("setShader(contextPtr, shaderPtrForSet, null, 0)", StringComparison.Ordinal);
    }
}
