/*
 * ⚠ 核心驅動未封裝、未簽章、未做執行驗證；本機編譯狀態以 Driver/build.bat 為準。
 *
 * IoctlHandler.c — DeviceIoControl 派發（patch-25 完整替換檔；P0-14 能力帳單）
 * 累積基底：xs318 現樹 IoctlHandler.c ＋ patch-20 的快照 IOCTL 契約兌現
 * （MSR_SNAPSHOT 整份 per-CPU 基線＋NOT_READY 語意、GET_BASELINE 回 BS_BASELINE_INFO）
 * ＋本補丁的 GET_CAPS 擴充（BS_DRIVER_CAPS，IoctlVersion 2）。
 *
 * 全域狀態宣告集中在 Common.h（g_MsrBaseline／g_MsrCount／g_MsrLock／g_Status*／…），
 * 此處不再重複（patch-20 修復：重複 extern 的限定詞會撞 volatile）。
 */
#include "Common.h"

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
            /* 新增一個 SHA-256 到阻擋清單（與雜湊工作項的讀取互斥） */
            if (inLen < 32) { status = STATUS_INVALID_PARAMETER; break; }
            KIRQL irql;
            KeAcquireSpinLock(&g_BlockLock, &irql);
            LONG idx = InterlockedIncrement(&g_BlockCount) - 1;
            if (idx >= BS_MAX_BLOCK_HASHES)
            {
                InterlockedDecrement(&g_BlockCount);
                KeReleaseSpinLock(&g_BlockLock, irql);
                status = STATUS_INSUFFICIENT_RESOURCES;
                break;
            }
            RtlCopyMemory(g_BlockList[idx].Sha256, buf, 32);
            g_BlockList[idx].Active = TRUE;
            KeReleaseSpinLock(&g_BlockLock, irql);
            break;
        }

        case IOCTL_BS_PROCESS_PROTECT:
        {
            /* 新增一個 PID 到受保護清單。
               〔P4-33〕兩處修正：
               1) 去重——同一 PID 反覆保護不再重複入列擠佔 64 名額（重複呼叫如實回成功，
                  冪等）；Bridge 端為單執行緒派遣迴圈，掃描＋插入之間無第二寫入者。
               2) 先寫槽位、後公開計數——讀端以 g_ProtectedCount 為快照界，「先寫後計數」
                  保證讀到計數就有完整資料；原順序（先 InterlockedIncrement 後寫槽）有
                  讀到尚未寫入槽位的窗口。 */
            if (inLen < sizeof(ULONG)) { status = STATUS_INVALID_PARAMETER; break; }
            ULONG pid = *(PULONG)buf;
            LONG count = g_ProtectedCount;
            BOOLEAN exists = FALSE;
            for (LONG i = 0; i < count && i < BS_MAX_PROTECTED_PIDS; i++)
            {
                if ((ULONG)(ULONG_PTR)g_ProtectedPids[i] == pid) { exists = TRUE; break; }
            }
            if (!exists)
            {
                if (count >= BS_MAX_PROTECTED_PIDS)
                {
                    status = STATUS_INSUFFICIENT_RESOURCES;
                    break;
                }
                g_ProtectedPids[count] = (HANDLE)(ULONG_PTR)pid;   /* 先寫槽位 */
                InterlockedIncrement(&g_ProtectedCount);           /* 後公開（release 屏障） */
            }
            break;
        }

        case IOCTL_BS_MSR_SNAPSHOT:
        {
            /* 〔複驗更正〕（patch-20 契約）回傳整份 per-CPU 基線——每 CPU 一整行
               BS_MAX_MSR_ENTRIES（16）條：前 MSR_COUNT（9）條為實際讀值、其後零填充；
               基線時讀失敗的條目 Value=0xFFFF…，消費端以 valid 對照。
               （原稿註解寫「CPU0 的 MSR_COUNT 條、CPU1 的…」——複製長度若按 9 條/CPU
               計會與 [cpu][16] 陣列的行距錯位；MsrSnapshotBaselineBytes 已改整行距。）
               基線未就緒如實回 NOT_READY——不再回空成功。 */
            if (!MsrSnapshotReady()) { status = STATUS_DEVICE_NOT_READY; break; }
            ULONG needed = MsrSnapshotBaselineBytes();
            if (needed == 0 || outLen < needed) { status = STATUS_BUFFER_TOO_SMALL; break; }
            KIRQL irql;
            KeAcquireSpinLock(&g_MsrLock, &irql);
            RtlCopyMemory(buf, g_MsrBaseline, needed);
            KeReleaseSpinLock(&g_MsrLock, irql);
            info = needed;
            break;
        }

        case IOCTL_BS_GET_CAPS:
        {
            /* （P0-14 能力帳單）回 BS_DRIVER_CAPS：FeatureMask＋DriverEntry 六步原始
               NTSTATUS＋MSR 快照活體健康＋基線/差異計數。usermode 據此判「部分就緒」，
               部分失敗不再被驅動整體 STATUS_SUCCESS 掩蓋。
               版本防護：Magic＋IoctlVersion 驗不過就讓 usermode 拒用（三態標示），不猜。 */
            if (outLen < sizeof(BS_DRIVER_CAPS)) { status = STATUS_BUFFER_TOO_SMALL; break; }
            PBS_DRIVER_CAPS caps = (PBS_DRIVER_CAPS)buf;
            RtlZeroMemory(caps, sizeof(BS_DRIVER_CAPS));
            caps->Magic        = BS_CAPS_MAGIC;
            caps->IoctlVersion = BS_IOCTL_VERSION;
            caps->FeatureMask  = g_CallbackCaps;
            caps->StatusLoadImage    = g_StatusLoadImage;
            caps->StatusProcess      = g_StatusProcess;
            caps->StatusRegistry     = g_StatusRegistry;
            caps->StatusObProtect    = g_StatusObProtect;
            caps->StatusHashProvider = g_StatusHashProvider;
            /* MSR 快照活體健康：init 失敗→原始失敗碼；在跑但基線未就緒（載入後
               0-5 秒）→DEVICE_NOT_READY；基線就緒→SUCCESS。 */
            if (!NT_SUCCESS(g_StatusMsrSnapshot))
                caps->StatusMsrSnapshot = g_StatusMsrSnapshot;
            else
                caps->StatusMsrSnapshot = MsrSnapshotReady()
                    ? STATUS_SUCCESS : STATUS_DEVICE_NOT_READY;
            KIRQL irql;
            KeAcquireSpinLock(&g_MsrLock, &irql);
            caps->MsrBaseEntries = (ULONG)g_MsrCount;
            caps->MsrDiffCount   = (ULONG)g_MsrDiffCount;
            KeReleaseSpinLock(&g_MsrLock, irql);
            caps->ActiveCpus = g_MsrActiveCpus;
            info = sizeof(BS_DRIVER_CAPS);
            break;
        }

        case IOCTL_BS_GET_BASELINE:
        {
            /* （patch-20 契約）回傳「基線規模＋差異數」（BS_BASELINE_INFO）——
               此前 g_MsrCount 恆 0，兩個快照 IOCTL 靜默回空。 */
            if (outLen < sizeof(BS_BASELINE_INFO)) { status = STATUS_BUFFER_TOO_SMALL; break; }
            KIRQL irql;
            KeAcquireSpinLock(&g_MsrLock, &irql);
            ((PBS_BASELINE_INFO)buf)->BaseEntries = (ULONG)g_MsrCount;
            ((PBS_BASELINE_INFO)buf)->DiffCount   = (ULONG)g_MsrDiffCount;
            KeReleaseSpinLock(&g_MsrLock, irql);
            info = sizeof(BS_BASELINE_INFO);
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
