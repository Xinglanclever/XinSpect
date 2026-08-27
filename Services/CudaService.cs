using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 偵測系統的 NVIDIA CUDA 驅動版本。透過 nvcuda.dll 的驅動 API（cuInit / cuDriverGetVersion）讀取，
/// 不需安裝 CUDA Toolkit，只要有 NVIDIA 顯示卡驅動即可。無 NVIDIA 平台時 DLL 不存在，回傳 null。
/// </summary>
public static class CudaService
{
    [DllImport("nvcuda", EntryPoint = "cuInit")]
    private static extern int CuInit(uint flags);

    [DllImport("nvcuda", EntryPoint = "cuDriverGetVersion")]
    private static extern int CuDriverGetVersion(out int version);

    /// <summary>回傳如 "12.4" 的 CUDA 版本；無 NVIDIA CUDA 驅動時回傳 null（呼叫端顯示為 ****）。</summary>
    public static string? DetectVersion()
    {
        try
        {
            if (CuInit(0) != 0) return null;                        // CUDA_SUCCESS == 0
            if (CuDriverGetVersion(out int v) != 0 || v <= 0) return null;
            // 版本整數格式：1000*major + 10*minor（例：12040 → 12.4）
            int major = v / 1000, minor = (v % 1000) / 10;
            return $"{major}.{minor}";
        }
        catch
        {
            // nvcuda.dll 不存在（無 NVIDIA 驅動）或呼叫失敗
            return null;
        }
    }
}
