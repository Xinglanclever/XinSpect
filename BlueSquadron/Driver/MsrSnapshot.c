/*
 * ⚠ 未編譯——需作者機驗證（WDK 編譯命令見 patch-25.md）
 *
 * MsrSnapshot.c — MSR 定期快照與比對（patch-25 完整替換檔）
 * 累積基底：patch-20 的 per-CPU 完整重寫（六類病灶修復，修復清單見下）不變；
 * 本補丁唯一改動＝P0-14 能力帳單：MsrSnapshotInit 從 VOID 改回 NTSTATUS
 * （DriverEntry 記入 g_StatusMsrSnapshot、GET_CAPS 回活體健康），其餘逐行同 patch-20。
 *
 * 每 5 秒把一枚 per-CPU DPC 排進「每一顆活躍核」，在目標核上讀關鍵安全 MSR，
 * 與該核自己的基線比對；任一 MSR 變更就寫入事件環形緩衝區（Critical）。
 *
 * 生命週期（本檔對驅動全域狀態的完整讀寫圖）：
 *   DriverEntry ──MsrSnapshotInit──▶ 初始化 s_Timer／s_Dpc（計時器 DPC）／s_CpuDpc[64]（逐核 DPC）
 *                                    查詢活躍核數 → g_MsrActiveCpus；KeSetTimerEx 每 5000ms 觸發 s_Dpc
 *   s_Dpc（TimerDpc）───────▶ 把 s_CpuDpc[0..g_MsrActiveCpus-1] 逐枚 KeInsertQueueDpc 釘到各自核心
 *   s_CpuDpc（CpuMsrDpc）──▶ 該核未建基線：寫入 g_MsrBaseline[cpu]／g_MsrBaseValid[cpu]，
 *                                    s_BaseReady[cpu]=TRUE，累加 g_MsrCount、
 *                                    s_BaselineReady 達標時置 s_Initialized=TRUE（全機基線就緒）
 *                            該核已建基線：逐條比對；差異 → BsPushEvent(Critical)＋
 *                                    g_MsrDiffCount++＋更新該條基線（只報第一次差異）
 *   IOCTL（IoctlHandler）──▶ GET_BASELINE／MSR_SNAPSHOT 在 g_MsrLock 保護下讀基線與計數
 *   DriverUnload ──MsrSnapshotStop──▶ s_Stopping=TRUE → KeCancelTimer → KeFlushQueuedDpcs
 *                                     （確保卸載前所有在飛 DPC 退場，防引用已卸載映像）
 *
 * 2026-11-07 修復清單（相對上一版）：
 *   1. s_Dpc 補回 KeInitializeDpc——上一版直接把未初始化（全零）的 KDPC 餵給 KeSetTimerEx，
 *      定時器首次到期（載入後 5 秒）就呼叫 NULL 常式 → 必死。
 *   2. s_Initialized 正確賦值——上一版全驅動無人寫 TRUE，CpuMsrDpc 的比對分支不可達，
 *      每輪都重鋪基線，竄改檢測死亡。現在：逐核 s_BaseReady 記帳，全數就緒即置 TRUE。
 *   3. s_Stopping 正確置位＋KeFlushQueuedDpcs——上一版永不為真且卸載不清隊列，
 *      卸載後 DPC 仍可能執行 → 引用已卸載驅動映像 → 死機。
 *   4. g_MsrCount 正確維護（基線 valid 條目數）＋新增 g_MsrDiffCount（差異次數）——
 *      上一版兩個快照 IOCTL 靜默回空（needed=0／恆 0）。
 *   5. per-CPU 語意補完：只對活躍核排 DPC（上一版固定排滿 64 核，對不存在的核
 *      KeSetTargetProcessorDpc 行為未定義）；基線就緒記帳改用獨立 s_BaseReady[cpu]
 *      （上一版拿 g_MsrBaseValid[cpu][0] 兼職，EFER 讀失敗的核會永遠重鋪基線）。
 *   6. 基線表讀寫加 s_MsrLock——上一版 DPC 寫、IOCTL 讀無任何互斥。
 *
 * 注意：__readmsr 在核心模式可直接呼叫，不需要 WinRing0。
 */
#include "Common.h"
#include <intrin.h>

static KTIMER    s_Timer;
static KDPC      s_Dpc;                 /* 計時器 DPC（Init 時 KeInitializeDpc，見修復 #1） */
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

/* per-CPU 基線：__readmsr 讀的是「當下核心」的暫存器，而 SPEC_CTRL（0x48）、
   FEATURE_CONTROL（0x3A）在真實硬體上逐核合法不同——全域單份基線會把正常差異
   報成 Critical「MSR 竄改」。所以基線必須逐核建立、逐核比對。 */
BS_MSR_ENTRY g_MsrBaseline[BS_MAX_CPU][BS_MAX_MSR_ENTRIES];
BOOLEAN      g_MsrBaseValid[BS_MAX_CPU][BS_MAX_MSR_ENTRIES];
volatile LONG g_MsrCount     = 0;   /* 全機基線 valid 條目數（GET_BASELINE 回「基線規模」） */
volatile LONG g_MsrDiffCount = 0;   /* 自基線建立以來偵測到的差異次數（GET_BASELINE 回「差異數」） */
ULONG        g_MsrActiveCpus = 0;   /* 全系統活躍核數（cap BS_MAX_CPU；多群組機僅覆蓋群組 0 前 64 核，見 Common.h） */
KSPIN_LOCK   g_MsrLock;             /* 基線表與計數的互斥（per-CPU DPC 寫 vs IOCTL 讀） */
ULONG        g_CallbackCaps = 0;

static KDPC      s_CpuDpc[BS_MAX_CPU];
static BOOLEAN   s_Initialized = FALSE;          /* 全機基線就緒（所有活躍核都建好基線） */
static BOOLEAN   s_Stopping = FALSE;             /* 卸載中：DPC 見此旗標即刻退出 */
static BOOLEAN   s_BaseReady[BS_MAX_CPU] = {0};  /* 逐核基線就緒記帳（見修復 #5） */
static volatile LONG s_BaselineReady = 0;        /* 已建基線的核數 */

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

    if (s_Stopping) return;                    /* 卸載中：即刻退出（見修復 #3） */

    ULONG cpu = KeGetCurrentProcessorNumber();
    if (cpu >= BS_MAX_CPU || cpu >= g_MsrActiveCpus) return;   /* 超出監控範圍的核不看 */

    BS_MSR_ENTRY current[BS_MAX_MSR_ENTRIES];
    BOOLEAN valid[BS_MAX_MSR_ENTRIES] = {0};
    ReadCurrentMsrs(current, valid);

    KIRQL irql;
    KeAcquireSpinLock(&g_MsrLock, &irql);      /* 基線表讀寫與 IOCTL 讀互斥（見修復 #6） */

    if (!s_BaseReady[cpu])
    {
        /* 這顆核的首次基線 */
        RtlCopyMemory(g_MsrBaseline[cpu], current, sizeof(current));
        RtlCopyMemory(g_MsrBaseValid[cpu], valid, sizeof(valid));
        s_BaseReady[cpu] = TRUE;

        LONG total = 0;
        for (ULONG i = 0; i < MSR_COUNT && i < BS_MAX_MSR_ENTRIES; i++)
            if (valid[i]) total++;
        InterlockedAdd(&g_MsrCount, total);    /* g_MsrCount 正確維護（見修復 #4） */
        LONG ready = InterlockedIncrement(&s_BaselineReady);
        KeReleaseSpinLock(&g_MsrLock, irql);

        if (!s_Initialized && ready >= (LONG)g_MsrActiveCpus)
        {
            s_Initialized = TRUE;              /* s_Initialized 正確賦值（見修復 #2） */
            DbgPrint("[BS] MSR baseline ready: %ld valid entries on %lu CPUs\n",
                     g_MsrCount, g_MsrActiveCpus);
        }
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
            detail[127] = L'\0';

            BsPushEvent(BsEventMsrChange, BsSevCritical,
                        (HANDLE)0, NULL, detail);
            DbgPrint("[BS] MSR tamper detected: %ws\n", detail);
            InterlockedIncrement(&g_MsrDiffCount);   /* 差異數契約（見修復 #4） */

            /* 更新該核基線（只報第一次差異，避免持續警報） */
            g_MsrBaseline[cpu][i].Value = current[i].Value;
        }
    }
    KeReleaseSpinLock(&g_MsrLock, irql);
}

/* 計時器 DPC：把每顆「活躍」核的 DPC 逐一排進各自的核心（見修復 #5：不再排滿 64 核） */
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

    for (ULONG cpu = 0; cpu < g_MsrActiveCpus && cpu < BS_MAX_CPU; cpu++)
    {
        KeSetTargetProcessorDpc(&s_CpuDpc[cpu], (CCHAR)cpu);
        KeInsertQueueDpc(&s_CpuDpc[cpu], NULL, NULL);
    }
}

NTSTATUS MsrSnapshotInit(void)
{
    s_Stopping = FALSE;
    s_Initialized = FALSE;
    s_BaselineReady = 0;
    RtlZeroMemory((PVOID)s_BaseReady, sizeof(s_BaseReady));
    RtlZeroMemory((PVOID)g_MsrBaseline, sizeof(g_MsrBaseline));
    RtlZeroMemory((PVOID)g_MsrBaseValid, sizeof(g_MsrBaseValid));
    g_MsrCount = 0;
    g_MsrDiffCount = 0;
    KeInitializeSpinLock(&g_MsrLock);

    /* 活躍核數（群組 0；BS_MAX_CPU=64 的單群組界線如 Common.h 註記——
       超過 64 核的機器只覆蓋前 64 核，其餘如實不覆蓋，不假裝全覆蓋） */
    g_MsrActiveCpus = KeQueryActiveProcessorCount(NULL);
    if (g_MsrActiveCpus > BS_MAX_CPU) g_MsrActiveCpus = BS_MAX_CPU;

    KeInitializeTimer(&s_Timer);
    KeInitializeDpc(&s_Dpc, TimerDpc, NULL);            /* 修復 #1：補回初始化 */
    for (ULONG cpu = 0; cpu < BS_MAX_CPU; cpu++)
        KeInitializeDpc(&s_CpuDpc[cpu], CpuMsrDpc, (PVOID)(ULONG_PTR)cpu);

    /* 首輪 5 秒後（相對到期時間），之後每 5000 ms 一輪 */
    LARGE_INTEGER due;
    due.QuadPart = -50000000LL;  /* 5 seconds in 100ns units, negative = relative */
    KeSetTimerEx(&s_Timer, due, 5000, &s_Dpc);
    s_Running = TRUE;
    DbgPrint("[BS] MSR snapshot started: %lu active CPUs, period 5s\n", g_MsrActiveCpus);
    /* P0-14：init 成敗回傳給 DriverEntry（記入 g_StatusMsrSnapshot、GET_CAPS 帶出）。
       現有路徑全部是記憶體／計時器初始化，沒有可失敗分支——回 SUCCESS 是如實；
       未來若加「活躍核數為 0 即停用」等降級路徑，這裡回對應失敗碼即可，能力位元
       與活體健康會跟著如實走。 */
    return STATUS_SUCCESS;
}

VOID MsrSnapshotStop(void)
{
    if (s_Running)
    {
        s_Stopping = TRUE;          /* 修復 #3：置位退出旗標（此前永不為真） */
        KeCancelTimer(&s_Timer);
        /* 等所有在飛／已入隊的 DPC 全部退場再返回——Unload 隨後會卸載映像，
           沒有這一步，DPC 可能在映像卸載後仍被派發（引用已釋放程式碼）。
           IRQL ≤ DISPATCH_LEVEL（本處 PASSIVE）即合法。 */
        KeFlushQueuedDpcs();
        s_Running = FALSE;
    }
}

/* ── IOCTL 查詢接縫（IoctlHandler.c 用；契約見 Common.h） ─────────────── */

BOOLEAN MsrSnapshotReady(void)
{
    return s_Initialized;
}

ULONG MsrSnapshotBaselineBytes(void)
{
    /* 〔複驗更正〕沿 patch-20 的同一步錯（行距 16 條 vs 長度按 9 條算）——平面複製會讓
       CPU1 起的 per-CPU 資料錯位。改整行距複製（BS_MAX_MSR_ENTRIES/CPU，零填充），
       與 g_MsrBaseline 的 [cpu][BS_MAX_MSR_ENTRIES] 佈局一致。 */
    return (ULONG)(g_MsrActiveCpus * BS_MAX_MSR_ENTRIES * sizeof(BS_MSR_ENTRY));
}
