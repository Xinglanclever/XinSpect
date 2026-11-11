/*
 * ⚠ 核心驅動未封裝、未簽章、未做執行驗證；本機編譯狀態以 Driver/build.bat 為準。
 *
 * Common.h — 共用結構與常數（patch-25 完整替換檔；P0-14 能力帳單）
 * 累積基底：xs318 現樹 Common.h ＋ patch-20 的 extern 契約（g_MsrCount volatile／
 * g_MsrDiffCount／g_MsrActiveCpus／g_MsrLock／BS_BASELINE_INFO／MsrSnapshotReady 宣告）
 * ＋本補丁的 BS_DRIVER_CAPS 能力帳單契約。
 */
#pragma once
#include <ntddk.h>
#include <bcrypt.h>
#include <ntstrsafe.h>

/* 進程存取權限常數：UM 的 winnt.h 有、KM 標頭沒帶——鷹架時代直接引用 UM 名稱從未編過 */
#ifndef PROCESS_TERMINATE
#define PROCESS_TERMINATE      0x0001
#endif
#ifndef PROCESS_CREATE_THREAD
#define PROCESS_CREATE_THREAD  0x0002
#endif
#ifndef PROCESS_VM_OPERATION
#define PROCESS_VM_OPERATION   0x0008
#endif
#ifndef PROCESS_VM_READ
#define PROCESS_VM_READ        0x0010
#endif
#ifndef PROCESS_VM_WRITE
#define PROCESS_VM_WRITE       0x0020
#endif
#ifndef PROCESS_DUP_HANDLE
#define PROCESS_DUP_HANDLE     0x0040
#endif

/* ── 事件環形緩衝區 ───────────────────────────────────────────────── */
#define BS_MAX_EVENTS          1024
#define BS_MAX_PATH_LEN        260
#define BS_MAX_BLOCK_HASHES    256
#define BS_MAX_PROTECTED_PIDS  64
#define BS_MAX_MSR_ENTRIES     16
#define BS_MAX_CPU             64    /* MSR 快照的 per-CPU 基線上限（單群組；超過則監控停用如實申報） */

typedef enum _BS_EVENT_TYPE {
    BsEventImageLoad = 1,
    BsEventProcessCreate = 2,
    BsEventProcessTerminate = 3,
    BsEventRegistryModify = 4,
    BsEventHandleAccess = 5,
    BsEventMsrChange = 6,
} BS_EVENT_TYPE;

typedef enum _BS_SEVERITY {
    BsSevInfo = 0,
    BsSevAdvisory = 1,
    BsSevWarning = 2,
    BsSevCritical = 3,
} BS_SEVERITY;

typedef struct _BS_EVENT {
    LARGE_INTEGER Timestamp;
    BS_EVENT_TYPE Type;
    BS_SEVERITY   Severity;
    ULONG         ProcessId;
    WCHAR         Path[BS_MAX_PATH_LEN];
    WCHAR         Detail[BS_MAX_PATH_LEN];
} BS_EVENT, *PBS_EVENT;

/* 阻擋清單 */
typedef struct _BS_BLOCK_ENTRY {
    UCHAR Sha256[32];
    BOOLEAN Active;
} BS_BLOCK_ENTRY;

/* MSR 快照 */
typedef struct _BS_MSR_ENTRY {
    ULONG  Msr;
    ULONGLONG Value;
} BS_MSR_ENTRY;

typedef struct _BS_MSR_DIFF {
    ULONG     Msr;
    ULONGLONG OldValue;
    ULONGLONG NewValue;
} BS_MSR_DIFF;

/* ── IOCTL 定義 ───────────────────────────────────────────────────── */
#define BS_DEVICE_TYPE       0x8337

#define IOCTL_BS_GET_EVENTS       CTL_CODE(BS_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_BS_SET_POLICY       CTL_CODE(BS_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_BS_MSR_SNAPSHOT     CTL_CODE(BS_DEVICE_TYPE, 0x803, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_BS_DRIVER_BLOCK     CTL_CODE(BS_DEVICE_TYPE, 0x804, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_BS_PROCESS_PROTECT  CTL_CODE(BS_DEVICE_TYPE, 0x805, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_BS_GET_BASELINE     CTL_CODE(BS_DEVICE_TYPE, 0x806, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_BS_GET_CAPS         CTL_CODE(BS_DEVICE_TYPE, 0x807, METHOD_BUFFERED, FILE_READ_DATA)

/* 能力帳單契約常數（P0-14；XsRegProbe XRP_DRIVER_INFO 同款設計）：
   Magic 防舊 usermode 對新版面誤讀；IoctlVersion 在版面變動時遞增，
   usermode 驗不過就拒用並三態標示，不猜。 */
#define BS_CAPS_MAGIC             0x31435342UL   /* 'BSC1' */
#define BS_IOCTL_VERSION          2              /* v2：GET_CAPS 從 4 位元組 ULONG 擴成 BS_DRIVER_CAPS */

/* 回呼註冊能力旗標（BS_DRIVER_CAPS.FeatureMask）——讓 usermode 核對「四個防護真的
   註冊上了嗎」，而不是驅動部分失敗仍回 STATUS_SUCCESS（2026-11-07 審計 P0-14：
   此前失敗只 DbgPrint，能力面完全不可見）。 */
#define BS_CAP_LOAD_IMAGE      0x01
#define BS_CAP_PROCESS         0x02
#define BS_CAP_REGISTRY        0x04
#define BS_CAP_OB_PROTECT      0x08
#define BS_CAP_MSR_SNAPSHOT    0x10

/* GET_BASELINE 回傳：基線規模＋差異數（契約兌現；patch-20 起，此前 g_MsrCount 恆 0 靜默回空） */
typedef struct _BS_BASELINE_INFO {
    ULONG BaseEntries;   /* 基線內 valid 條目數（全機） */
    ULONG DiffCount;     /* 自基線建立以來偵測到的差異次數 */
} BS_BASELINE_INFO, *PBS_BASELINE_INFO;

/* 能力帳單回覆（IOCTL_BS_GET_CAPS；x64 全 4 位元組對齊＝84 位元組，無隱式填充）：
   DriverEntry 每一步的原始 NTSTATUS 存進對應 g_Status*，usermode 從一個 IOCTL 就能
   回答「驅動載入成功，但哪些防護其實沒起來、為什麼」。StatusMsrSnapshot 是活體健康
   （基線就緒＝SUCCESS、尚未就緒＝DEVICE_NOT_READY、init 失敗＝原始失敗碼），
   不是 DriverEntry 當下的瞬時值。 */
typedef struct _BS_DRIVER_CAPS {
    ULONG    Magic;              /* BS_CAPS_MAGIC */
    ULONG    IoctlVersion;       /* BS_IOCTL_VERSION */
    ULONG    FeatureMask;        /* BS_CAP_* 聯集 */
    NTSTATUS StatusLoadImage;    /* PsSetLoadImageNotifyRoutine 的 DriverEntry 原始回傳 */
    NTSTATUS StatusProcess;      /* PsSetCreateProcessNotifyRoutineEx 的原始回傳 */
    NTSTATUS StatusRegistry;     /* CmRegisterCallbackEx 的原始回傳 */
    NTSTATUS StatusObProtect;    /* ObRegisterCallbacks 的原始回傳 */
    NTSTATUS StatusMsrSnapshot;  /* 活體健康：見上註 */
    NTSTATUS StatusHashProvider; /* BCryptOpenAlgorithmProvider 的原始回傳 */
    ULONG    MsrBaseEntries;     /* 全機基線 valid 條目數（＝g_MsrCount） */
    ULONG    MsrDiffCount;       /* 自基線以來偵測到的差異次數（＝g_MsrDiffCount） */
    ULONG    ActiveCpus;         /* MSR 快照覆蓋的活躍核數（cap BS_MAX_CPU） */
} BS_DRIVER_CAPS, *PBS_DRIVER_CAPS;

/* ── 全域變數（定義於標註的 .c 檔） ───────────────────────────────── */
extern BS_EVENT       g_EventRing[BS_MAX_EVENTS];
extern volatile LONG  g_EventHead;
extern KSPIN_LOCK     g_EventLock;

extern BS_BLOCK_ENTRY g_BlockList[BS_MAX_BLOCK_HASHES];
extern volatile LONG  g_BlockCount;
extern KSPIN_LOCK     g_BlockLock;
extern BS_MSR_ENTRY g_MsrBaseline[BS_MAX_CPU][BS_MAX_MSR_ENTRIES];   /* per-CPU 基線 */
extern BOOLEAN      g_MsrBaseValid[BS_MAX_CPU][BS_MAX_MSR_ENTRIES];  /* 基線時讀失敗的條目不參與比對 */
extern volatile LONG g_MsrCount;      /* 全機基線 valid 條目數（GET_BASELINE 的「基線規模」） */
extern volatile LONG g_MsrDiffCount;   /* 自基線建立以來偵測到的 MSR 差異次數 */
extern ULONG         g_MsrActiveCpus;  /* 全系統活躍核數（cap BS_MAX_CPU；多群組機僅覆蓋群組 0 前 64 核） */
extern KSPIN_LOCK    g_MsrLock;        /* 基線表互斥（per-CPU DPC 寫 vs IOCTL 讀） */
extern ULONG        g_CallbackCaps;

/* DriverEntry 各步狀態（P0-14：定義在 BlueSquadronDriver.c；不再只 DbgPrint——
   每一步的原始 NTSTATUS 都會被 GET_CAPS 帶出去，部分失敗如實可見） */
extern NTSTATUS g_StatusLoadImage;
extern NTSTATUS g_StatusProcess;
extern NTSTATUS g_StatusRegistry;
extern NTSTATUS g_StatusObProtect;
extern NTSTATUS g_StatusMsrSnapshot;
extern NTSTATUS g_StatusHashProvider;

/* SHA-256 演算法物件（BCrypt；Callbacks.c 持有、DriverEntry 開 Unload 關） */
extern PVOID         g_HashProvider;
extern LARGE_INTEGER g_CmCookie;   /* CmRegisterCallbackEx 的 cookie（登錄回呼取路徑用） */

extern HANDLE         g_ProtectedPids[BS_MAX_PROTECTED_PIDS];
extern volatile LONG  g_ProtectedCount;

/* 寫入事件的輔助函式 */
extern VOID BsPushEvent(BS_EVENT_TYPE type, BS_SEVERITY sev,
                        HANDLE pid, PCUNICODE_STRING path, PCWSTR detail);

/* MSR 快照狀態查詢（IoctlHandler 用；實作在 MsrSnapshot.c） */
extern BOOLEAN MsrSnapshotReady(void);
extern ULONG   MsrSnapshotBaselineBytes(void);
