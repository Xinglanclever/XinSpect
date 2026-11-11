/*
 * XsRegProbe.c — XinSpect 白名單受限唯讀暫存器探測驅動（WDM，迷你兄弟驅動）
 *
 * 定位（深層暫存器計畫 Phase 3）：
 *   - 唯讀：只提供 MSR 批次讀與 MMIO 區塊讀，兩者皆過允許清單；不存在任何寫入路徑。
 *   - 退出即卸載：不註冊任何回呼、不常駐、無內部狀態——usermode 關閉控制代碼並停止服務後即完全退場
 *     （與 BlueSquadron「留著保護」相反，故分出獨立兄弟驅動而非共用）。
 *   - 管理員限定：IoCreateDeviceSecure + SDDL 只給 Builtin Administrators 完整存取。
 *   - 版本化 + 能力協商：IOCTL_XRP_QUERY_INFO 回 IoctlVersion/FeatureMask，usermode 不符即拒用。
 *   - 資料驅動：允許清單在 XrpContract.h 的常數表；加範圍＝改表不改派遣碼。
 *
 * 特權層極薄：所有位元解讀都在 managed 純解碼器；驅動只把「允許清單內」的位元組誠實帶回來，
 * 讀不到（例外／映射失敗）就回對應 NTSTATUS，絕不回 0 頂替成功。
 */
#include <ntddk.h>
#include <wdmsec.h>   /* IoCreateDeviceSecure 的宣告與實作（wdmsec.lib）在 Win10 之後不會由 ntddk.h 帶進來 */
#include <intrin.h>
#include "XrpContract.h"

#define XRP_DEVICE_NAME L"\\Device\\XsRegProbe"
#define XRP_SYMLINK_NAME L"\\DosDevices\\XsRegProbe"

static PDEVICE_OBJECT g_DeviceObject = NULL;

static NTSTATUS XrpDispatch(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp);

/* ── 允許清單查驗（線性掃描；表為唯讀常數、呼叫均在 PASSIVE_LEVEL，無需鎖） ── */

static BOOLEAN XrpMsrAllowed(_In_ ULONG msr)
{
    for (ULONG i = 0; i < RTL_NUMBER_OF(g_MsrAllow); i++)
        if (g_MsrAllow[i] == msr) return TRUE;
    for (ULONG i = 0; i < RTL_NUMBER_OF(g_MsrRangeAllow); i++)
        if (msr >= g_MsrRangeAllow[i].Lo && msr <= g_MsrRangeAllow[i].Hi) return TRUE;
    return FALSE;
}

static BOOLEAN XrpMmioRangeAllowed(_In_ ULONGLONG phys, _In_ ULONG length)
{
    if (length == 0 || length > XRP_MMIO_MAX) return FALSE;
    ULONGLONG last = phys + length - 1;
    if (last < phys) return FALSE; /* 溢位防護 */
    for (ULONG i = 0; i < RTL_NUMBER_OF(g_MmioAllow); i++)
        if (phys >= g_MmioAllow[i].Lo && last <= g_MmioAllow[i].Hi) return TRUE;
    return FALSE;
}

/* ── 特權讀取（特權層極薄：失敗如實回碼，不造值） ── */

static NTSTATUS XrpReadMsrSafe(_In_ ULONG msr, _Out_ ULONGLONG* value)
{
    *value = 0;
    __try
    {
        *value = __readmsr((unsigned long)msr);
        return STATUS_SUCCESS;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return GetExceptionCode(); /* 平台沒有該 MSR 時如實回例外碼，由 usermode 標三態 */
    }
}

static NTSTATUS XrpReadMmioBlock(_In_ ULONGLONG phys, _In_ ULONG length, _Out_writes_bytes_(length) UCHAR* out)
{
    if (!XrpMmioRangeAllowed(phys, length)) return STATUS_ACCESS_DENIED;

    PHYSICAL_ADDRESS pa;
    pa.QuadPart = (LONGLONG)phys;
    PVOID mapped = MmMapIoSpaceEx(pa, length, PAGE_READONLY | PAGE_NOCACHE);
    /* 只限制這個虛擬映射上的意外寫入；不代表實體位址的其他映射也唯讀。 */
    if (mapped == NULL) return STATUS_INSUFFICIENT_RESOURCES;

    RtlCopyMemory(out, mapped, length);
    MmUnmapIoSpace(mapped, length);
    return STATUS_SUCCESS;
}

/* ── IOCTL 實作 ── */

static NTSTATUS XrpQueryInfo(_Out_ XRP_DRIVER_INFO* info)
{
    RtlZeroMemory(info, sizeof(*info));
    info->Magic          = XRP_MAGIC;
    info->IoctlVersion   = XRP_IOCTL_VERSION;
    info->FeatureMask    = XRP_FEATURE_MSR_READ | XRP_FEATURE_MMIO_READ;
    info->MsrExactCount  = RTL_NUMBER_OF(g_MsrAllow);
    info->MsrRangeCount  = RTL_NUMBER_OF(g_MsrRangeAllow);
    info->MmioRangeCount = RTL_NUMBER_OF(g_MmioAllow);
    return STATUS_SUCCESS;
}

static NTSTATUS XrpReadMsrList(_In_ const XRP_MSR_REQUEST* req, _In_ ULONG inLen,
                               _Out_writes_bytes_(outBytes) XRP_MSR_SLOT* slots,
                               _In_ ULONG outBytes, _Out_ ULONG* replyBytes)
{
    /* 槽位基底＝請求頭之後：契約（XrpContract.h:53-57「Slots[Count] 緊隨其後」）與
       managed 端（DriverMsrReader.cs:139-142 打包、:157-160 解析）都以 offset 8 為槽 0。
       呼叫端（派遣）必須把 slots 指到 buffer + sizeof(XRP_MSR_REQUEST)。 */
    if (inLen < sizeof(XRP_MSR_REQUEST)) return STATUS_INVALID_PARAMETER;
    ULONG count = req->Count;
    if (count == 0 || count > XRP_MSR_MAX_BATCH) return STATUS_INVALID_PARAMETER;

    /* count ≤ 64 → slotsBytes ≤ 1024，加法不可能溢位 ULONG。
       inLen 也要覆蓋整個槽位陣列：METHOD_BUFFERED 的 SystemBuffer 只有前 inLen 位元組
       由 I/O 管理器拷入，越過 inLen 讀 Msr 欄＝讀未初始化集區記憶體。 */
    ULONG slotsBytes = count * (ULONG)sizeof(XRP_MSR_SLOT);
    if (inLen < sizeof(XRP_MSR_REQUEST) + slotsBytes ||
        outBytes < sizeof(XRP_MSR_REQUEST) + slotsBytes)
        return STATUS_INVALID_PARAMETER;

    for (ULONG i = 0; i < count; i++)
    {
        XRP_MSR_SLOT* slot = &slots[i];
        ULONG msr = slot->Msr;                   /* 先取輸入欄，再回填同槽（in/out 同塊不重排） */
        slot->Status = STATUS_ACCESS_DENIED;     /* 允許清單外：明確拒絕，不回 0 值 */
        slot->Value = 0;
        if (!XrpMsrAllowed(msr)) continue;
        slot->Status = XrpReadMsrSafe(msr, &slot->Value);
    }
    *replyBytes = sizeof(XRP_MSR_REQUEST) + slotsBytes;
    return STATUS_SUCCESS;
}

static NTSTATUS XrpReadMmio(_In_ const XRP_MMIO_REQUEST* req, _In_ ULONG inLen,
                            _Out_ XRP_MMIO_REPLY* reply, _In_ ULONG outBytes, _Out_ ULONG* replyBytes)
{
    if (inLen < sizeof(XRP_MMIO_REQUEST)) return STATUS_INVALID_PARAMETER;
    if (outBytes < FIELD_OFFSET(XRP_MMIO_REPLY, Data)) return STATUS_INVALID_PARAMETER;

    reply->Length = 0;
    ULONG length = req->Length;
    if (length == 0 || length > XRP_MMIO_MAX || outBytes < FIELD_OFFSET(XRP_MMIO_REPLY, Data) + length)
        return STATUS_INVALID_PARAMETER;

    NTSTATUS status = XrpReadMmioBlock(req->PhysicalAddress, length, reply->Data);
    if (!NT_SUCCESS(status)) return status; /* 不回填 Data：讀不到就明說，不給半成品 */

    reply->Status = status;
    reply->Length = length;
    *replyBytes = FIELD_OFFSET(XRP_MMIO_REPLY, Data) + length;
    return STATUS_SUCCESS;
}

/* ── 派遣 ── */

static NTSTATUS XrpDispatch(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);
    NTSTATUS status = STATUS_SUCCESS;
    ULONG replyBytes = 0;

    switch (irpSp->MajorFunction)
    {
    case IRP_MJ_CREATE:
    case IRP_MJ_CLOSE:
        break;

    case IRP_MJ_DEVICE_CONTROL:
    {
        PVOID buffer = Irp->AssociatedIrp.SystemBuffer;
        ULONG inLen = irpSp->Parameters.DeviceIoControl.InputBufferLength;
        ULONG outLen = irpSp->Parameters.DeviceIoControl.OutputBufferLength;

        switch (irpSp->Parameters.DeviceIoControl.IoControlCode)
        {
        case IOCTL_XRP_QUERY_INFO:
            if (outLen < sizeof(XRP_DRIVER_INFO)) { status = STATUS_INVALID_PARAMETER; break; }
            status = XrpQueryInfo((XRP_DRIVER_INFO*)buffer);
            if (NT_SUCCESS(status)) replyBytes = sizeof(XRP_DRIVER_INFO);
            break;

        case IOCTL_XRP_READ_MSR_LIST:
            /* 槽位基底＝請求頭（8 位元組）之後——與 XrpContract.h 及 managed 端 DriverMsrReader 對齊 */
            status = XrpReadMsrList((const XRP_MSR_REQUEST*)buffer, inLen,
                                    (XRP_MSR_SLOT*)((PUCHAR)buffer + sizeof(XRP_MSR_REQUEST)),
                                    outLen, &replyBytes);
            break;

        case IOCTL_XRP_READ_MMIO:
            status = XrpReadMmio((const XRP_MMIO_REQUEST*)buffer, inLen,
                                 (XRP_MMIO_REPLY*)buffer, outLen, &replyBytes);
            break;

        default:
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
        }
        break;
    }

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = NT_SUCCESS(status) ? replyBytes : 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

/* ── 進出場：無任何常駐回呼，Unload 即完全退場 ── */

static void XrpUnload(_In_ PDRIVER_OBJECT DriverObject)
{
    UNICODE_STRING symLink = RTL_CONSTANT_STRING(XRP_SYMLINK_NAME);
    IoDeleteSymbolicLink(&symLink);
    if (DriverObject->DeviceObject != NULL) IoDeleteDevice(DriverObject->DeviceObject);
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    UNREFERENCED_PARAMETER(RegistryPath);
    DriverObject->DriverUnload = XrpUnload;
    DriverObject->MajorFunction[IRP_MJ_CREATE]         = XrpDispatch;
    DriverObject->MajorFunction[IRP_MJ_CLOSE]          = XrpDispatch;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = XrpDispatch;

    UNICODE_STRING devName = RTL_CONSTANT_STRING(XRP_DEVICE_NAME);
    UNICODE_STRING symLink = RTL_CONSTANT_STRING(XRP_SYMLINK_NAME);
    UNICODE_STRING sddl = RTL_CONSTANT_STRING(L"D:P(A;;GA;;;BA)(A;;GR;;;BU)");
    /* 管理員完整存取＋Users 唯讀（2026-11-07）：本驅動白名單唯讀、無任何寫入路徑，
       開 Users 讀讓 asInvoker 的主程序在服務常駐後免提權即可讀——濫用面由
       允許清單（位址白名單）收口，不在 SDDL。 */
    /* 裝置類別要的是 GUID，不是字串：先前把 UNICODE_STRING 傳給 LPCGUID 參數，
       編譯器只給 C4133，執行期會拿指標內容當 GUID 讀（裝置類別變成垃圾）。
       4d36e97d-… 是 System 類別。 */
    static const GUID XrpDevClassGuid =
        { 0x4d36e97d, 0xe325, 0x11ce, { 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18 } };

    NTSTATUS status = IoCreateDeviceSecure(
        DriverObject, 0, &devName,
        FILE_DEVICE_UNKNOWN, FILE_DEVICE_SECURE_OPEN, FALSE,
        &sddl, &XrpDevClassGuid,
        &g_DeviceObject);
    if (!NT_SUCCESS(status)) return status;

    status = IoCreateSymbolicLink(&symLink, &devName);
    if (!NT_SUCCESS(status))
    {
        IoDeleteDevice(g_DeviceObject);
        return status;
    }

    return STATUS_SUCCESS;
}
