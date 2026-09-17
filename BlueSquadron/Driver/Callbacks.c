/*
 * Callbacks.c — 各類核心回呼實作
 */
#include "Common.h"

/* ── 全域事件環形緩衝區 ───────────────────────────────────────────── */
BS_EVENT      g_EventRing[BS_MAX_EVENTS] = {0};
volatile LONG g_EventHead = 0;
KSPIN_LOCK    g_EventLock;

/* ── 阻擋清單與受保護進程 ─────────────────────────────────────────── */
BS_BLOCK_ENTRY g_BlockList[BS_MAX_BLOCK_HASHES] = {0};
volatile LONG  g_BlockCount = 0;

HANDLE        g_ProtectedPids[BS_MAX_PROTECTED_PIDS] = {0};
volatile LONG g_ProtectedCount = 0;

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
VOID OnImageLoad(
    _In_opt_ PUNICODE_STRING FullImageName,
    _In_ HANDLE ProcessId,
    _In_ PIMAGE_INFO ImageInfo)
{
    UNREFERENCED_PARAMETER(ImageInfo);

    /* 只關注核心模式載入（ProcessId == 0 → 核心驅動） */
    if (ProcessId != (HANDLE)0) return;

    BS_SEVERITY sev = BsSevInfo;

    /*
     * Phase 2：計算載入映像的 SHA-256 並比對阻擋清單。
     * 核心內計算檔案雜湊需要 ZwCreateFile + ZwReadFile + BCrypt，
     * 這裡暫時只記錄事件。
     */

    BsPushEvent(BsEventImageLoad, sev, ProcessId, FullImageName, L"核心驅動載入");
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
    UNREFERENCED_PARAMETER(Argument2);

    REG_NOTIFY_CLASS notifyClass = (REG_NOTIFY_CLASS)(ULONG_PTR)Argument1;

    /* 只關注 SetValue（有人改登錄檔） */
    if (notifyClass == RegNtPreSetValueKey)
    {
        /* Phase 2：比對關鍵安全設定路徑，阻擋或記錄 */
        /* 目前僅記錄 */
    }

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
