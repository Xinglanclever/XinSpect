/*
 * Callbacks.c — 各類核心回呼實作
 *
 * Phase 2 已落地（2026-11-07）：
 *   1. 映像載入：核心模式映像入隊工作項，BCrypt 算 SHA-256 與阻擋清單比對——
 *      命中推 Critical 事件。雜湊在專屬工作項裡做（回呼內不能做檔案 I/O）。
 *   2. 登錄檔：RegNtPreSetValueKey 取完整路徑，比對敏感前綴表（Run 鍵／Services／
 *      LSA／SafeBoot／Session Manager／使用者 Run）——命中推 Warning 事件。
 *      界線：只記錄不阻擋——pre-notification 可以否決，但擋掉一筆合法寫入
 *      就是系統事故；阻擋需要 opt-in 開關與白名單，留給下一階段。
 */
#include <ntddk.h>
#include <bcrypt.h>
#include "Common.h"

/* ── 全域事件環形緩衝區 ───────────────────────────────────────────── */
BS_EVENT      g_EventRing[BS_MAX_EVENTS] = {0};
volatile LONG g_EventHead = 0;
KSPIN_LOCK    g_EventLock;

/* ── 阻擋清單與受保護進程 ─────────────────────────────────────────── */
BS_BLOCK_ENTRY g_BlockList[BS_MAX_BLOCK_HASHES] = {0};
volatile LONG  g_BlockCount = 0;
KSPIN_LOCK     g_BlockLock;                    /* 清單讀寫互斥（IOCTL 寫 vs 工作項讀） */

HANDLE        g_ProtectedPids[BS_MAX_PROTECTED_PIDS] = {0};
volatile LONG g_ProtectedCount = 0;

/* SHA-256 演算法物件（DriverEntry 開、Unload 關；全部回呼共用） */
PVOID g_HashProvider = NULL;

/* 寫入一筆事件 */
VOID BsPushEvent(BS_EVENT_TYPE type, BS_SEVERITY sev,
                 HANDLE pid, PCUNICODE_STRING path, PCWSTR detail)
{
    KIRQL irql;
    KeAcquireSpinLock(&g_EventLock, &irql);

    LONG idx = InterlockedIncrement(&g_EventHead) % BS_MAX_EVENTS;
    PBS_EVENT e = &g_EventRing[idx >= 0 ? idx : idx + BS_MAX_EVENTS];
    KeQuerySystemTimePrecise(&e->Timestamp);
    e->Type = type;
    e->Severity = sev;
    e->ProcessId = (ULONG)(ULONG_PTR)pid;

    if (path != NULL && path->Buffer != NULL && path->Length > 0)
    {
        USHORT copyLen = min(path->Length, (BS_MAX_PATH_LEN - 1) * sizeof(WCHAR));
        RtlCopyMemory(e->Path, path->Buffer, copyLen);
        e->Path[copyLen / sizeof(WCHAR)] = L'\0';
    }
    else
        e->Path[0] = L'\0';

    if (detail != NULL)
    {
        SIZE_T len = wcslen(detail);
        if (len >= BS_MAX_PATH_LEN) len = BS_MAX_PATH_LEN - 1;
        RtlCopyMemory(e->Detail, detail, len * sizeof(WCHAR));
        e->Detail[len] = L'\0';
    }
    else
        e->Detail[0] = L'\0';

    KeReleaseSpinLock(&g_EventLock, irql);
}

/* ── 1. 驅動/DLL 載入回呼 ──────────────────────────────────────────── */

/* 雜湊工作項：回呼內不能做檔案 I/O（loader 鎖），複製路徑後丟到系統工作執行緒慢慢算 */
typedef struct _BS_HASH_WORK {
    WORK_QUEUE_ITEM WorkItem;
    WCHAR           Path[BS_MAX_PATH_LEN];
} BS_HASH_WORK, *PBS_HASH_WORK;

static VOID BsHashWorker(PVOID Context)
{
    PBS_HASH_WORK w = (PBS_HASH_WORK)Context;

    UNICODE_STRING uPath;
    RtlInitUnicodeString(&uPath, w->Path);

    OBJECT_ATTRIBUTES oa;
    InitializeObjectAttributes(&oa, &uPath,
                               OBJ_KERNEL_HANDLE | OBJ_CASE_INSENSITIVE, NULL, NULL);

    HANDLE file = NULL;
    IO_STATUS_BLOCK iosb = {0};
    NTSTATUS st = ZwCreateFile(&file,
                               FILE_READ_DATA | SYNCHRONIZE, &oa, &iosb,
                               NULL, FILE_ATTRIBUTE_NORMAL,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                               FILE_OPEN,
                               FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE,
                               NULL, 0);
    if (!NT_SUCCESS(st)) { ExFreePoolWithTag(w, 'BSHM'); return; }

    /* 讀整檔算 SHA-256（核心驅動檔通常 < 1MB；分頁池緩衝一次 64K） */
    PUCHAR buf = (PUCHAR)ExAllocatePoolWithTag(NonPagedPool, 65536, 'BSHB');
    BCRYPT_HASH_HANDLE hash = NULL;
    UCHAR sha[32] = {0};
    BOOLEAN hashed = FALSE;

    if (buf != NULL &&
        NT_SUCCESS(BCryptCreateHash((BCRYPT_ALG_HANDLE)g_HashProvider,
                                    &hash, NULL, 0, NULL, 0, 0)))
    {
        BOOLEAN readErr = FALSE;
        for (;;)
        {
            st = ZwReadFile(file, NULL, NULL, NULL, &iosb, buf, 65536, NULL, NULL);
            if (!NT_SUCCESS(st)) { readErr = TRUE; break; }
            if (iosb.Information == 0) break;                       /* EOF */
            BCryptHashData(hash, buf, (ULONG)iosb.Information, 0);
        }
        if (!readErr)
            hashed = NT_SUCCESS(BCryptFinishHash(hash, sha, sizeof(sha), 0));
    }
    if (hash != NULL) BCryptDestroyHash(hash);
    if (buf != NULL) ExFreePoolWithTag(buf, 'BSHB');
    ZwClose(file);

    /* 比對阻擋清單（短暫鎖；比對完才推事件，不在鎖內呼叫會拿別把鎖的函式） */
    BOOLEAN hit = FALSE;
    if (hashed)
    {
        KIRQL irql;
        KeAcquireSpinLock(&g_BlockLock, &irql);
        for (LONG i = 0; i < g_BlockCount && i < BS_MAX_BLOCK_HASHES; i++)
        {
            if (g_BlockList[i].Active &&
                RtlEqualMemory(g_BlockList[i].Sha256, sha, 32))
            {
                hit = TRUE;
                break;
            }
        }
        KeReleaseSpinLock(&g_BlockLock, irql);
    }

    if (hit)
        BsPushEvent(BsEventImageLoad, BsSevCritical, NULL, &uPath,
                    L"核心驅動載入命中阻擋清單（BYOVD）");

    ExFreePoolWithTag(w, 'BSHM');
}

VOID OnImageLoad(
    _In_opt_ PUNICODE_STRING FullImageName,
    _In_ HANDLE ProcessId,
    _In_ PIMAGE_INFO ImageInfo)
{
    UNREFERENCED_PARAMETER(ImageInfo);

    /* 只關注核心模式載入（ProcessId == 0 → 核心驅動）——使用者模式 DLL 由
       使用者態的 ETW 通道（EtwWatcher）覆蓋，核心裡不重複算 */
    if (ProcessId != (HANDLE)0) return;
    if (g_HashProvider == NULL) return;
    if (g_BlockCount <= 0) return;                     /* 清單空：不浪費工作項 */
    if (FullImageName == NULL || FullImageName->Length == 0) return;

    PBS_HASH_WORK w = (PBS_HASH_WORK)ExAllocatePoolWithTag(
        NonPagedPool, sizeof(BS_HASH_WORK), 'BSHM');
    if (w == NULL) return;

    RtlZeroMemory(w, sizeof(*w));
    USHORT copyLen = min(FullImageName->Length, (BS_MAX_PATH_LEN - 1) * sizeof(WCHAR));
    RtlCopyMemory(w->Path, FullImageName->Buffer, copyLen);
    w->Path[copyLen / sizeof(WCHAR)] = L'\0';

    ExInitializeWorkItem(&w->WorkItem, BsHashWorker, w);
    ExQueueWorkItem(&w->WorkItem, DelayedWorkQueue);
}

/* ── 2. 進程建立回呼 ───────────────────────────────────────────────── */
VOID OnProcessNotify(
    _Inout_ PEPROCESS Process,
    _In_ HANDLE ProcessId,
    _Inout_opt_ PPS_CREATE_NOTIFY_INFO CreateInfo)
{
    UNREFERENCED_PARAMETER(Process);

    if (CreateInfo != NULL)
    {
        /* 進程建立 */
        BsPushEvent(BsEventProcessCreate, BsSevInfo, ProcessId,
                    CreateInfo->ImageFileName, L"進程建立");
    }
    else
    {
        /* 進程終止 */
        BsPushEvent(BsEventProcessTerminate, BsSevInfo, ProcessId, NULL, L"進程終止");
    }
}

/* ── 3. 登錄檔回呼 ─────────────────────────────────────────────────── */
NTSTATUS OnRegistryCallback(
    _In_ PVOID CallbackContext,
    _In_opt_ PVOID Argument1,
    _In_opt_ PVOID Argument2)
{
    UNREFERENCED_PARAMETER(CallbackContext);

    if ((REG_NOTIFY_CLASS)(ULONG_PTR)Argument1 != RegNtPreSetValueKey)
        return STATUS_SUCCESS;

    PREG_SET_VALUE_KEY_INFORMATION info = (PREG_SET_VALUE_KEY_INFORMATION)Argument2;
    if (info == NULL || info->Object == NULL || info->ValueName == NULL)
        return STATUS_SUCCESS;

    /* 取完整登錄路徑（g_CmCookie 是 CmRegisterCallbackEx 給的） */
    PCUNICODE_STRING keyPath = NULL;
    if (!NT_SUCCESS(CmCallbackGetKeyObjectIDEx(&g_CmCookie, info->Object,
                                               &keyPath, NULL)))
        return STATUS_SUCCESS;
    if (keyPath == NULL || keyPath->Buffer == NULL || keyPath->Length == 0)
    {
        CmCallbackReleaseKeyObjectIDEx(keyPath);
        return STATUS_SUCCESS;
    }

    /* 敏感前綴表：持續性（Run／Services）與安全策略（LSA／SafeBoot／Session Manager）。
       比對不分大小寫。 */
    static const PCWSTR critical[] = {
        L"\\REGISTRY\\MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run",
        L"\\REGISTRY\\MACHINE\\SYSTEM\\CurrentControlSet\\Services",
        L"\\REGISTRY\\MACHINE\\SYSTEM\\CurrentControlSet\\Control\\LSA",
        L"\\REGISTRY\\MACHINE\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot",
        L"\\REGISTRY\\MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Session Manager",
    };
    BOOLEAN hit = FALSE;
    for (ULONG i = 0; i < RTL_NUMBER_OF(critical) && !hit; i++)
    {
        UNICODE_STRING p;
        RtlInitUnicodeString(&p, critical[i]);
        hit = RtlPrefixUnicodeString(&p, keyPath, TRUE);
    }

    /* 使用者持續性：\REGISTRY\USER\<SID>\...\CurrentVersion\Run —— 計數範圍內掃子字串 */
    if (!hit)
    {
        UNICODE_STRING user;
        RtlInitUnicodeString(&user, L"\\REGISTRY\\USER\\");
        if (RtlPrefixUnicodeString(&user, keyPath, TRUE))
        {
            ULONG wchars = keyPath->Length / sizeof(WCHAR);
            for (ULONG i = 0; i + 18 <= wchars; i++)
            {
                if (keyPath->Buffer[i]     == L'\\' &&
                    keyPath->Buffer[i + 1] == L'C')
                {
                    UNICODE_STRING seg;
                    seg.Buffer = &keyPath->Buffer[i];
                    seg.Length = 18 * sizeof(WCHAR);
                    seg.MaximumLength = seg.Length;
                    UNICODE_STRING run;
                    RtlInitUnicodeString(&run, L"\\CurrentVersion\\Run");
                    if (RtlEqualUnicodeString(&seg, &run, TRUE)) { hit = TRUE; break; }
                }
            }
        }
    }

    if (hit)
    {
        /* Detail ＝ 值名稱（受 260 上限保護） */
        WCHAR detail[BS_MAX_PATH_LEN];
        ULONG vn = info->ValueName->Length / sizeof(WCHAR);
        if (vn > BS_MAX_PATH_LEN - 1) vn = BS_MAX_PATH_LEN - 1;
        RtlCopyMemory(detail, info->ValueName->Buffer, vn * sizeof(WCHAR));
        detail[vn] = L'\0';
        BsPushEvent(BsEventRegistryModify, BsSevWarning,
                    PsGetCurrentProcessId(), keyPath, detail);
    }

    CmCallbackReleaseKeyObjectIDEx(keyPath);
    return STATUS_SUCCESS;
}

/* ── 4. 物件存取回呼（保護 LSASS 等受保護進程） ────────────────────── */
OB_PREOP_CALLBACK_STATUS OnObPreOp(
    _In_ PVOID RegistrationContext,
    _Inout_ POB_PRE_OPERATION_INFORMATION OperationInfo)
{
    UNREFERENCED_PARAMETER(RegistrationContext);

    if (OperationInfo->ObjectType != *PsProcessType)
        return OB_PREOP_SUCCESS;

    /* 取得目標進程的 PID */
    PEPROCESS target = (PEPROCESS)OperationInfo->Object;
    HANDLE targetPid = PsGetProcessId(target);

    /* 檢查是否在受保護 PID 清單中 */
    LONG count = g_ProtectedCount;
    for (LONG i = 0; i < count && i < BS_MAX_PROTECTED_PIDS; i++)
    {
        if (g_ProtectedPids[i] == targetPid)
        {
            /* 移除危險的存取權限 */
            if (OperationInfo->Operation == OB_OPERATION_HANDLE_CREATE)
            {
                OperationInfo->Parameters->CreateHandleInformation.DesiredAccess
                    &= ~(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION
                         | PROCESS_DUP_HANDLE | PROCESS_CREATE_THREAD);
            }
            BsPushEvent(BsEventHandleAccess, BsSevWarning, targetPid,
                        NULL, L"受保護進程的控制代碼存取被限制");
            break;
        }
    }

    return OB_PREOP_SUCCESS;
}
