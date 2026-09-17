/*
 * Common.h — 共用結構與常數
 */
#pragma once
#include <ntddk.h>

/* ── 事件環形緩衝區 ───────────────────────────────────────────────── */
#define BS_MAX_EVENTS          1024
#define BS_MAX_PATH_LEN        260
#define BS_MAX_BLOCK_HASHES    256
#define BS_MAX_PROTECTED_PIDS  64
#define BS_MAX_MSR_ENTRIES     16

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

/* ── 全域變數（定義在 Callbacks.c） ───────────────────────────────── */
extern BS_EVENT       g_EventRing[BS_MAX_EVENTS];
extern volatile LONG  g_EventHead;
extern KSPIN_LOCK     g_EventLock;

extern BS_BLOCK_ENTRY g_BlockList[BS_MAX_BLOCK_HASHES];
extern volatile LONG  g_BlockCount;

extern HANDLE         g_ProtectedPids[BS_MAX_PROTECTED_PIDS];
extern volatile LONG  g_ProtectedCount;

/* 寫入事件的輔助函式 */
extern VOID BsPushEvent(BS_EVENT_TYPE type, BS_SEVERITY sev,
                        HANDLE pid, PCUNICODE_STRING path, PCWSTR detail);
