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

/* per-CPU 基線（2026-11-07 重寫）：__readmsr 讀的是「當下核心」的暫存器，而清單裡的
   SPEC_CTRL（0x48）、FEATURE_CONTROL（0x3A）在真實硬體上逐核合法不同——全域單份基線
   會把正常差異報成 Critical「MSR 竄改」，安全警報器自己生產假警報（比無警報更傷）。
   現在：每處理器一枚 DPC（KeSetTargetProcessorDpc 釘核）＋每處理器一份基線；
   計時器 DPC 只負責把各核的 DPC 排進去。 */
BS_MSR_ENTRY g_MsrBaseline[BS_MAX_CPU][BS_MAX_MSR_ENTRIES];
BOOLEAN      g_MsrBaseValid[BS_MAX_CPU][BS_MAX_MSR_ENTRIES];
LONG         g_MsrCount = 0;
ULONG        g_CallbackCaps = 0;

static KDPC      s_CpuDpc[BS_MAX_CPU];
static BOOLEAN   s_Initialized = FALSE;
static BOOLEAN   s_Stopping = FALSE;

static VOID ReadCurrentMsrs(BS_MSR_ENTRY* out, BOOLEAN* valid)
{
    for (ULONG i = 0; i < MSR_COUNT && i < BS_MAX_MSR_ENTRIES; i++)
    {
        out[i].Msr = CriticalMsrs[i];
        __try {
            out[i].Value = __readmsr(CriticalMsrs[i]);
            valid[i] = TRUE;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            out[i].Value = (ULONGLONG)-1;  /* IOCTL 相容的舊表示；比對時以 valid 跳過，不毒化基線 */
            valid[i] = FALSE;
        }
    }
}

/* 在目標核心上執行（DPC 已釘核）：建立／比對「這顆核」的基線 */
static VOID NTAPI CpuMsrDpc(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID Arg1,
    _In_opt_ PVOID Arg2)
{
    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(DeferredContext);
    UNREFERENCED_PARAMETER(Arg1);
    UNREFERENCED_PARAMETER(Arg2);

    if (s_Stopping) return;

    ULONG cpu = KeGetCurrentProcessorNumber();
    if (cpu >= BS_MAX_CPU) return;

    BS_MSR_ENTRY current[BS_MAX_MSR_ENTRIES];
    BOOLEAN valid[BS_MAX_MSR_ENTRIES] = {0};
    ReadCurrentMsrs(current, valid);

    if (!s_Initialized || !g_MsrBaseValid[cpu][0])
    {
        /* 這顆核的首次基線 */
        RtlCopyMemory(g_MsrBaseline[cpu], current, sizeof(current));
        RtlCopyMemory(g_MsrBaseValid[cpu], valid, sizeof(valid));
        return;
    }

    for (ULONG i = 0; i < MSR_COUNT && i < BS_MAX_MSR_ENTRIES; i++)
    {
        /* 基線時讀失敗的條目不參與比對（-1 是「沒讀到」，不是一個值——
           暫時性例外不能毒化基線，之後的真值不該被報成竄改） */
        if (!g_MsrBaseValid[cpu][i] || !valid[i]) continue;
        if (current[i].Value != g_MsrBaseline[cpu][i].Value)
        {
            WCHAR detail[128];
            _snwprintf(detail, 128,
                       L"CPU %u MSR 0x%X: 0x%016llX -> 0x%016llX",
                       cpu, current[i].Msr, g_MsrBaseline[cpu][i].Value, current[i].Value);

            BsPushEvent(BsEventMsrChange, BsSevCritical,
                        (HANDLE)0, NULL, detail);

            /* 更新該核基線（只報第一次差異，避免持續警報） */
            g_MsrBaseline[cpu][i].Value = current[i].Value;
        }
    }
}

/* 計時器 DPC：把每核的 DPC 逐一排進各自的核心 */
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

    if (s_Stopping) return;

    for (ULONG cpu = 0; cpu < BS_MAX_CPU; cpu++)
    {
        KeSetTargetProcessorDpc(&s_CpuDpc[cpu], (CCHAR)cpu);
        KeInsertQueueDpc(&s_CpuDpc[cpu], NULL, NULL);
    }
}

VOID MsrSnapshotInit(void)
{
    KeInitializeTimer(&s_Timer);
    for (ULONG cpu = 0; cpu < BS_MAX_CPU; cpu++)
        KeInitializeDpc(&s_CpuDpc[cpu], CpuMsrDpc, (PVOID)(ULONG_PTR)cpu);

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
