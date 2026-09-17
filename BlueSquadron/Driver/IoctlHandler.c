/*
 * IoctlHandler.c — DeviceIoControl 派發
 */
#include "Common.h"

extern BS_MSR_ENTRY g_MsrBaseline[BS_MAX_MSR_ENTRIES];
extern LONG         g_MsrCount;

NTSTATUS IoctlDispatch(
    _In_ PDEVICE_OBJECT DeviceObject,
    _Inout_ PIRP Irp)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);
    NTSTATUS status = STATUS_SUCCESS;
    ULONG_PTR info = 0;

    switch (irpSp->MajorFunction)
    {
    case IRP_MJ_CREATE:
    case IRP_MJ_CLOSE:
        status = STATUS_SUCCESS;
        break;

    case IRP_MJ_DEVICE_CONTROL:
    {
        ULONG code = irpSp->Parameters.DeviceIoControl.IoControlCode;
        PVOID buf = Irp->AssociatedIrp.SystemBuffer;
        ULONG inLen = irpSp->Parameters.DeviceIoControl.InputBufferLength;
        ULONG outLen = irpSp->Parameters.DeviceIoControl.OutputBufferLength;

        switch (code)
        {
        case IOCTL_BS_GET_EVENTS:
        {
            /* 回傳最近的事件（盡可能填滿輸出緩衝區） */
            ULONG maxEvents = outLen / sizeof(BS_EVENT);
            if (maxEvents == 0) { status = STATUS_BUFFER_TOO_SMALL; break; }
            if (maxEvents > BS_MAX_EVENTS) maxEvents = BS_MAX_EVENTS;

            KIRQL irql;
            KeAcquireSpinLock(&g_EventLock, &irql);
            LONG head = g_EventHead;
            LONG start = head - (LONG)maxEvents;
            if (start < 0) start = 0;
            ULONG count = 0;
            for (LONG i = start; i < head && count < maxEvents; i++)
            {
                LONG idx = i % BS_MAX_EVENTS;
                if (idx < 0) idx += BS_MAX_EVENTS;
                RtlCopyMemory((PBS_EVENT)buf + count, &g_EventRing[idx], sizeof(BS_EVENT));
                count++;
            }
            KeReleaseSpinLock(&g_EventLock, irql);
            info = count * sizeof(BS_EVENT);
            break;
        }

        case IOCTL_BS_DRIVER_BLOCK:
        {
            /* 新增一個 SHA-256 到阻擋清單 */
            if (inLen < 32) { status = STATUS_INVALID_PARAMETER; break; }
            LONG idx = InterlockedIncrement(&g_BlockCount) - 1;
            if (idx >= BS_MAX_BLOCK_HASHES)
            {
                InterlockedDecrement(&g_BlockCount);
                status = STATUS_INSUFFICIENT_RESOURCES;
                break;
            }
            RtlCopyMemory(g_BlockList[idx].Sha256, buf, 32);
            g_BlockList[idx].Active = TRUE;
            break;
        }

        case IOCTL_BS_PROCESS_PROTECT:
        {
            /* 新增一個 PID 到受保護清單 */
            if (inLen < sizeof(ULONG)) { status = STATUS_INVALID_PARAMETER; break; }
            ULONG pid = *(PULONG)buf;
            LONG idx = InterlockedIncrement(&g_ProtectedCount) - 1;
            if (idx >= BS_MAX_PROTECTED_PIDS)
            {
                InterlockedDecrement(&g_ProtectedCount);
                status = STATUS_INSUFFICIENT_RESOURCES;
                break;
            }
            g_ProtectedPids[idx] = (HANDLE)(ULONG_PTR)pid;
            break;
        }

        case IOCTL_BS_MSR_SNAPSHOT:
        {
            /* 回傳目前 MSR 基線 */
            ULONG needed = (ULONG)(g_MsrCount * sizeof(BS_MSR_ENTRY));
            if (outLen < needed) { status = STATUS_BUFFER_TOO_SMALL; break; }
            RtlCopyMemory(buf, g_MsrBaseline, needed);
            info = needed;
            break;
        }

        case IOCTL_BS_GET_BASELINE:
        {
            /* 回傳 MSR 基線 + 差異數量 */
            if (outLen < sizeof(LONG)) { status = STATUS_BUFFER_TOO_SMALL; break; }
            *(PLONG)buf = g_MsrCount;
            info = sizeof(LONG);
            break;
        }

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
    Irp->IoStatus.Information = info;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}
