/*
 * XrpContract.h — XsRegProbe 的 IOCTL 契約與資料驅動允許清單
 *
 * 版本化 + 能力協商：usermode 先送 IOCTL_XRP_QUERY_INFO 拿 IoctlVersion 與 FeatureMask，
 * 再依能力挑用 IOCTL。未來加新版欄位＝IoctlVersion+1、新能力＝FeatureMask 加位元，舊呼叫端不受影響。
 *
 * 加一條新 MSR／MMIO 範圍＝只改本檔的 g_MsrAllow / g_MsrRangeAllow / g_MmioAllow 表，
 * 派遣碼一律不動（資料驅動，不寫特例）。
 *
 * 本驅動唯讀：沒有任何寫入路徑。MSR 經 __readmsr（允許清單外一律 ACCESS_DENIED），
 * MMIO 經 MmMapIoSpace 映射允許清單內的範圍、讀完立刻解除映射。所有表為唯讀常數。
 * PCI 設定空間讀取暫不納入（BUS_INTERFACE_STANDARD 查詢的載入風險先在開發機驗證）；
 * 這一步 managed 端現階段仍走 WinRing0（見 XinSpect 深層暫存器計畫移交文件 §5.7 退役順序）。
 */
#pragma once

/* ── IOCTL 定義 ─────────────────────────────────────────────────────
 * 裝置型別 0x8338（BlueSquadron 用 0x8337，兄弟驅動取下一號）。
 * METHOD_BUFFERED + FILE_READ_DATA：緩衝往返單一 SystemBuffer，開啟控制代碼即需讀取權（SDDL 只給管理員）。 */
#define XRP_DEVICE_TYPE          0x8338

#define IOCTL_XRP_QUERY_INFO     CTL_CODE(XRP_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_XRP_READ_MSR_LIST  CTL_CODE(XRP_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_XRP_READ_MMIO      CTL_CODE(XRP_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_READ_DATA)

/* ── 能力位元 ─────────────────────────────────────────────────────── */
#define XRP_FEATURE_MSR_READ     0x00000001UL
#define XRP_FEATURE_MMIO_READ    0x00000002UL

/* 契約常數 */
#define XRP_MAGIC                0x31505258UL   /* 'XRP1' */
#define XRP_IOCTL_VERSION        1
#define XRP_MSR_MAX_BATCH        64             /* 單次 READ_MSR_LIST 的槽位上限 */
#define XRP_MMIO_MAX             4096           /* 單次 READ_MMIO 的位元組上限 */

/* 能力協商回覆：usermode 開場先讀這個，不符就拒用、退回三態標示 */
typedef struct _XRP_DRIVER_INFO {
    ULONG Magic;             /* XRP_MAGIC */
    ULONG IoctlVersion;      /* XRP_IOCTL_VERSION */
    ULONG FeatureMask;       /* XRP_FEATURE_* */
    ULONG MsrExactCount;     /* 允許清單筆數（診斷用，讓 usermode 能核對同一份表） */
    ULONG MsrRangeCount;
    ULONG MmioRangeCount;
} XRP_DRIVER_INFO, *PXRP_DRIVER_INFO;

/* MSR 批次槽位：in——Msr；out——Status/Value 由驅動回填（同槽往返，不重排） */
typedef struct _XRP_MSR_SLOT {
    ULONG     Msr;
    ULONG     Status;        /* NTSTATUS；允許清單外＝STATUS_ACCESS_DENIED，讀取例外＝該例外碼 */
    ULONGLONG Value;
} XRP_MSR_SLOT, *PXRP_MSR_SLOT;

typedef struct _XRP_MSR_REQUEST {
    ULONG Count;             /* 槽位數，1..XRP_MSR_MAX_BATCH */
    ULONG Reserved;
    /* XRP_MSR_SLOT Slots[Count] 緊隨其後（METHOD_BUFFERED 同一塊進出） */
} XRP_MSR_REQUEST, *PXRP_MSR_REQUEST;

typedef struct _XRP_MMIO_REQUEST {
    ULONGLONG PhysicalAddress;
    ULONG     Length;        /* 1..XRP_MMIO_MAX，整段必須落在單一允許範圍內 */
    ULONG     Reserved;
} XRP_MMIO_REQUEST, *PXRP_MMIO_REQUEST;

typedef struct _XRP_MMIO_REPLY {
    ULONG Status;            /* NTSTATUS */
    ULONG Length;            /* 實際讀到並回填 Data 的位元組數 */
    UCHAR Data[XRP_MMIO_MAX];
} XRP_MMIO_REPLY, *PXRP_MMIO_REPLY;

/* ── 資料驅動允許清單（唯讀常數；加範圍＝改表不改碼） ───────────────── */

typedef struct _XRP_MSR_RANGE { ULONG Lo; ULONG Hi; } XRP_MSR_RANGE;           /* 含端點 */

/* MSR 逐條白名單：取自 XinSpect managed 端實際讀取的集合（Services/CpuMsrFacts、CeilingService、
 * CoreTempMapService、McaService、UncorePmuService 等）。讀不到的 MSR 交由 __try 例外路徑如實回傳，
 * 不用 0 頂替。 */
static const ULONG g_MsrAllow[] = {
    0x10,   /* MSR_TSC */
    0x34,   /* MSR_SMI_COUNT */
    0x48,   /* 平台頻率（managed 端既有引用） */
    0x8B,   /* IA32_BIOS_SIGN_ID（微碼版本） */
    0xC1,   /* IA32_PMC0 */
    0xCE,   /* MSR_PLATFORM_INFO（倍頻上限/下限） */
    0x186,  /* IA32_PERFEVTSEL0 */
    0x198,  /* IA32_PERF_STATUS（現行倍頻） */
    0x19C,  /* IA32_THERM_STATUS（降頻因果：熱節流位元） */
    0x1A2,  /* MSR_TEMPERATURE_TARGET（TjMax） */
    0x1AA,  /* MSR_MISC_PWR_MGMT */
    0x1AD,  /* MSR_TURBO_RATIO_LIMIT */
    0x1AE,  /* MSR_TURBO_RATIO_LIMIT_CORES */
    0x1B1,  /* MSR_PKG_THERM_STATUS */
    0x1FC,  /* MSR_POWER_CTL */
    0x38F,  /* IA32_PERF_GLOBAL_CTRL */
    0x606,  /* MSR_RAPL_POWER_UNIT */
    0x610,  /* MSR_PKG_POWER_LIMIT */
    0x611,  /* MSR_PKG_ENERGY_STATUS */
    0x613,  /* MSR_PKG_PERF_STATUS */
    0x614,  /* MSR_PKG_POWER_INFO */
    0x619,  /* MSR_DRAM_ENERGY_STATUS */
    0x620,  /* MSR_UNCORE_RATIO_LIMIT */
    0x621,  /* MSR_UNCORE_PERF_STATUS */
    0x638,  /* MSR_PP0_POWER_LIMIT */
    0x639,  /* MSR_PP0_ENERGY_STATUS */
    0x64F,  /* MSR_LIMIT_REASONS（server） */
    0x690,  /* MSR_LIMIT_REASONS（client） */
    0x770,  /* IA32_PM_ENABLE（HWP） */
    0x771,  /* IA32_HWP_CAPABILITIES */
    0xC8D,  /* MSR_QM_EVTSEL */
    0xC8E,  /* MSR_QM_CTR */
    0xC8F,  /* MSR_PQR_ASSOC */
    0x3A,   /* IA32_FEATURE_CONTROL（PlatformSecurityMsrService） */
    0xC80,  /* IA32_DEBUG_INTERFACE（PlatformSecurityMsrService） */
    0xE7,   /* IA32_MPERF */
    0xE8,   /* IA32_APERF */
};

/* MSR 範圍白名單：MCA 銀行（McaService 以 0x401+bank*4 讀 IA32_MCi_STATUS） */
static const XRP_MSR_RANGE g_MsrRangeAllow[] = {
    { 0x400, 0x4FF },
};

/* MMIO 範圍白名單（含端點；整個請求必須落在單一範圍內）：
 * - SPIBAR：PCH 固定映射 FED10000 起（Intel PCH EDS）；XinSpect 只讀 0x00..0x87
 *   （HSFSTS/FRAP/FREG0-5/PR0-4）。實際 BAR 由 managed 端自 PCI 0:1F.5+0x10 取得後對表，
 *   不在表內就三態標示，不硬讀。
 * - ECAM：PCIe 擴充組態空間（唯讀、無副作用），基底須與平台 MCFG 一致——
 *   managed 端以 MCFG 對帳，不一致就三態標示。 */
typedef struct _XRP_MMIO_RANGE { ULONGLONG Lo; ULONGLONG Hi; } XRP_MMIO_RANGE;

static const XRP_MMIO_RANGE g_MmioAllow[] = {
    { 0xFED10000ULL, 0xFED10087ULL },
    { 0xE0000000ULL, 0xE7FFFFFFULL },
};
