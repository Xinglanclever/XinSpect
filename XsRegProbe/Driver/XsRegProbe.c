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
    PVOID mapped = MmMapIoSpace(pa, length, MmNonCached);
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
                               _Out_ XRP_MSR_SLOT* slots, _In_ ULONG outBytes, _Out_ ULONG* replyBytes)
{
    if (inLen < sizeof(XRP_MSR_REQUEST)) return STATUS_INVALID_PARAMETER;
    ULONG count = req->Count;
    if (count == 0 || count > XRP_MSR_MAX_BATCH) return STATUS_INVALID_PARAMETER;
    if (outBytes < sizeof(XRP_MSR_REQUEST) + count * sizeof(XRP_MSR_SLOT)) return STATUS_INVALID_PARAMETER;

    for (ULONG i = 0; i < count; i++)
    {
        slots[i].Status = STATUS_ACCESS_DENIED; /* 允許清單外：明確拒絕，不回 0 值 */
        slots[i].Value = 0;
        if (!XrpMsrAllowed(slots[i].Msr)) continue;
        slots[i].Status = XrpReadMsrSafe(slots[i].Msr, &slots[i].Value);
    }
    *replyBytes = sizeof(XRP_MSR_REQUEST) + count * sizeof(XRP_MSR_SLOT);
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
            status = XrpReadMsrList((const XRP_MSR_REQUEST*)buffer, inLen,
                                    (XRP_MSR_SLOT*)buffer, outLen, &replyBytes);
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
    UNICODE_STRING sddl = RTL_CONSTANT_STRING(L"D:P(A;;GA;;;BA)"); /* 僅管理員完整存取（比照 BlueSquadron） */
    UNICODE_STRING classGuid = RTL_CONSTANT_STRING(L"{4d36e97d-e325-11ce-bfc1-08002be10318}");

    NTSTATUS status = IoCreateDeviceSecure(
        DriverObject, 0, &devName,
        FILE_DEVICE_UNKNOWN, FILE_DEVICE_SECURE_OPEN, FALSE,
        &sddl, &classGuid,
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
