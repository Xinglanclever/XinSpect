/*
 * MsrSnapshot.c — MSR 定期快照與比對
 *
 * 每 5 秒讀取一組關鍵安全 MSR 並比對上次快照。
 * 若任一 MSR 值變更，寫入事件環形緩衝區。
 *
 * 注意：__readmsr 在核心模式可直接呼叫，不需要 WinRing0。
 */
#include "Common.h"
#include <intrin.h>

static KTIMER    s_Timer;
static KDPC      s_Dpc;
static BOOLEAN   s_Running = FALSE;

/* 關鍵安全 MSR 清單 */
static const ULONG CriticalMsrs[] = {
    0xC0000080,  /* EFER (NX, LME, SCE) */
    0xC0000081,  /* STAR (syscall) */
    0xC0000082,  /* LSTAR (syscall entry) */
    0xC0000083,  /* CSTAR (compat syscall) */
    0xC0000084,  /* SFMASK */
    0x1A0,       /* IA32_MISC_ENABLE */
    0x48,        /* IA32_SPEC_CTRL */
    0x10A,       /* IA32_ARCH_CAPABILITIES */
    0x3A,        /* IA32_FEATURE_CONTROL (VMX lock) */
};

#define MSR_COUNT (sizeof(CriticalMsrs) / sizeof(CriticalMsrs[0]))

/* 全域基線（也用於 IOCTL 回傳） */
BS_MSR_ENTRY g_MsrBaseline[BS_MAX_MSR_ENTRIES];
LONG         g_MsrCount = 0;

static BOOLEAN s_Initialized = FALSE;

static VOID ReadCurrentMsrs(BS_MSR_ENTRY* out)
{
    for (ULONG i = 0; i < MSR_COUNT && i < BS_MAX_MSR_ENTRIES; i++)
    {
        out[i].Msr = CriticalMsrs[i];
        __try {
            out[i].Value = __readmsr(CriticalMsrs[i]);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            out[i].Value = (ULONGLONG)-1;  /* 讀不到就標記為 -1 */
        }
    }
}

static VOID NTAPI TimerDpc(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID Arg1,
    _In_opt_ PVOID Arg2)
{
    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(DeferredContext);
    UNREFERENCED_PARAMETER(Arg1);
    UNREFERENCED_PARAMETER(Arg2);

    BS_MSR_ENTRY current[BS_MAX_MSR_ENTRIES];
    ReadCurrentMsrs(current);

    if (!s_Initialized)
    {
        /* 首次：建立基線 */
        RtlCopyMemory(g_MsrBaseline, current, sizeof(current));
        g_MsrCount = (LONG)MSR_COUNT;
        s_Initialized = TRUE;
        return;
    }

    /* 比對 */
    for (ULONG i = 0; i < MSR_COUNT; i++)
    {
        if (current[i].Value != g_MsrBaseline[i].Value)
        {
            WCHAR detail[128];
            _snwprintf(detail, 128,
                       L"MSR 0x%X: 0x%016llX -> 0x%016llX",
                       current[i].Msr, g_MsrBaseline[i].Value, current[i].Value);

            BsPushEvent(BsEventMsrChange, BsSevCritical,
                        (HANDLE)0, NULL, detail);

            /* 更新基線（只報第一次差異，避免持續警報） */
            g_MsrBaseline[i].Value = current[i].Value;
        }
    }
}

VOID MsrSnapshotInit(void)
{
    KeInitializeSpinLock(&g_EventLock);
    KeInitializeTimer(&s_Timer);
    KeInitializeDpc(&s_Dpc, TimerDpc, NULL);

    /* 每 5 秒觸發一次 */
    LARGE_INTEGER period;
    period.QuadPart = -50000000LL;  /* 5 seconds in 100ns units, negative = relative */
    KeSetTimerEx(&s_Timer, period, 5000, &s_Dpc);
    s_Running = TRUE;
}

VOID MsrSnapshotStop(void)
{
    if (s_Running)
    {
        KeCancelTimer(&s_Timer);
        s_Running = FALSE;
    }
}
