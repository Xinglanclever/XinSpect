using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

internal readonly record struct RawMousePacket(nint Device, int X, int Y, ushort Flags);

internal interface IRawMouseNativeApi : IDisposable
{
    bool TryGetMouseRegistration(out nint target, out string? error);
    bool Register(nint target, bool remove, out string? error);
    bool TryRead(nint rawInput, out RawMousePacket packet, out string? error);
    (string Id, bool Readable) Identify(nint device);
}

/// <summary>Win32 公開 Raw Input API；不開 HID handle、不寫裝置、不注入輸入。</summary>
internal sealed class RawMouseNativeApi : IRawMouseNativeApi
{
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const int BufferBytes = 4096;
    private nint _buffer = Marshal.AllocHGlobal(BufferBytes);

    // 官方 API 契約：
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getregisteredrawinputdevices
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerrawinputdevices
    public bool TryGetMouseRegistration(out nint target, out string? error)
    {
        target = 0; error = null;
        uint count = 0, size = (uint)Marshal.SizeOf<RawInputDevice>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue)
        { error = Win32Error("查詢既有 Raw Input 註冊"); return false; }
        if (count > 1024) { error = "Raw Input 註冊數超過安全上限。"; return false; }
        if (count == 0) return true;
        var devices = new RawInputDevice[count];
        uint read = GetRegisteredRawInputDevices(devices, ref count, size);
        if (read == uint.MaxValue || read > devices.Length)
        { error = Win32Error("讀取既有 Raw Input 註冊"); return false; }
        foreach (var device in devices.Take((int)read))
            if (device.UsagePage == 1 && device.Usage == 2)
            {
                // 即使 hwndTarget = NULL 也已存在應用程式註冊，不得搶占。
                target = device.Target;
                if (target == 0) { error = "本程序已有預設目標的 Raw Input 滑鼠註冊；未覆寫。"; return false; }
                return true;
            }
        return true;
    }

    public bool Register(nint target, bool remove, out string? error)
    {
        // 僅前景；沒有 INPUTSINK、NOLEGACY 或 CAPTUREMOUSE，不攔截正常滑鼠操作。
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = remove ? 1u : 0x2000u, Target = remove ? 0 : target };
        bool ok = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        error = ok ? null : Win32Error(remove ? "移除 Raw Input 註冊" : "註冊 Raw Input 滑鼠");
        return ok;
    }

    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getrawinputdata
    public unsafe bool TryRead(nint rawInput, out RawMousePacket packet, out string? error)
    {
        packet = default; error = null;
        if (_buffer == 0) { error = "Raw Input 緩衝區已釋放。"; return false; }
        uint bytes = BufferBytes;
        uint read = GetRawInputData(rawInput, RidInput, _buffer, ref bytes, (uint)(8 + 2 * IntPtr.Size));
        if (read == uint.MaxValue) { error = Win32Error("讀取 WM_INPUT"); return false; }
        if (read > BufferBytes || !TryDecode(new ReadOnlySpan<byte>((void*)_buffer, (int)read), IntPtr.Size, out packet))
        { error = "WM_INPUT 長度／型別無效；未猜測資料。"; return false; }
        return true;
    }

    // RAWINPUTHEADER 與 RAWMOUSE 的 Windows SDK 佈局（x86/x64）。
    // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-rawinputheader
    // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-rawmouse
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, int pointerBytes, out RawMousePacket packet)
    {
        packet = default;
        if (pointerBytes != 4 && pointerBytes != 8) return false;
        int header = 8 + 2 * pointerBytes;
        if (bytes.Length < header + 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0) return false;
        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (declaredSize != header + 24 || declaredSize > bytes.Length) return false;
        long device = pointerBytes == 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes[8..])
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (IntPtr.Size == 4 && (device < 0 || device > uint.MaxValue)) return false;
        packet = new((nint)device, BinaryPrimitives.ReadInt32LittleEndian(bytes[(header + 12)..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[(header + 16)..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[header..]));
        return true;
    }

    // 只存雜湊後的本窗識別；裝置介面路徑可能含序號，不顯示、不匯出原文。
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getrawinputdeviceinfow
    public (string Id, bool Readable) Identify(nint device)
    {
        if (device == 0) return ("system-unknown", false);
        uint count = 0;
        if (GetRawInputDeviceInfoW(device, RidiDeviceName, null, ref count) == uint.MaxValue || count == 0 || count > 4096)
            return ($"session-{device.ToInt64():X}", false);
        var name = new StringBuilder((int)count + 1);
        uint result = GetRawInputDeviceInfoW(device, RidiDeviceName, name, ref count);
        if (result == uint.MaxValue || name.Length == 0) return ($"session-{device.ToInt64():X}", false);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(name.ToString())))[..12];
        return ($"raw-{hash}", true);
    }

    public void Dispose()
    {
        nint buffer = Interlocked.Exchange(ref _buffer, 0);
        if (buffer != 0) Marshal.FreeHGlobal(buffer);
    }

    private static string Win32Error(string operation) => $"{operation}失敗（Win32 {Marshal.GetLastWin32Error()}）。";

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public nint Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] RawInputDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(nint device, uint command, StringBuilder? data, ref uint count);
}
