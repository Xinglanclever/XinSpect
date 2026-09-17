/*
 * BlueSquadron.sys — 藍色中隊核心驅動
 *
 * WDM 核心驅動，提供以下回呼：
 *   1. PsSetLoadImageNotifyRoutine     — 驅動/DLL 載入偵測（BYOVD 攔截）
 *   2. PsSetCreateProcessNotifyRoutineEx — 進程建立/終止偵測
 *   3. CmRegisterCallbackEx             — 登錄檔操作偵測
 *   4. ObRegisterCallbacks              — 物件控制代碼存取偵測（LSASS 保護）
 *   5. MSR 定期快照比對                  — 核心暫存器完整性
 *
 * 編譯需求：
 *   - Visual Studio Build Tools 2022+ 含 C++ 桌面工作負載
 *   - Windows Driver Kit (WDK) 10.0.26100.0+
 *   - 測試簽章：bcdedit /set testsigning on
 *
 * 編譯命令（在 x64 Native Tools Command Prompt 中）：
 *   cl /kernel /W4 /WX /GS- /Zl /Oi /Gy /D_AMD64_ /DAMD64
 *      /DNTDDI_VERSION=0x0A000009 /D_WIN32_WINNT=0x0A00
 *      /I"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\km"
 *      /c BlueSquadronDriver.c Callbacks.c IoctlHandler.c MsrSnapshot.c
 *   link /DRIVER:WDM /SUBSYSTEM:NATIVE /ENTRY:DriverEntry
 *      /OUT:BlueSquadron.sys
 *      /LIBPATH:"C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\km\x64"
 *      ntoskrnl.lib hal.lib wdmsec.lib
 *      BlueSquadronDriver.obj Callbacks.obj IoctlHandler.obj MsrSnapshot.obj
 *
 * 安裝（以系統管理員身分）：
 *   sc create BlueSquadron type= kernel binPath= "<path>\BlueSquadron.sys"
 *   sc start BlueSquadron
 *
 * 卸載：
 *   sc stop BlueSquadron
 *   sc delete BlueSquadron
 */

#include <ntddk.h>
#include <wdmsec.h>
#include "Common.h"

/* 前置宣告（實作在其他 .c 檔） */
extern VOID OnImageLoad(PUNICODE_STRING FullImageName, HANDLE ProcessId, PIMAGE_INFO ImageInfo);
extern VOID OnProcessNotify(PEPROCESS Process, HANDLE ProcessId, PPS_CREATE_NOTIFY_INFO CreateInfo);
extern NTSTATUS OnRegistryCallback(PVOID Context, PVOID Arg1, PVOID Arg2);
extern NTSTATUS IoctlDispatch(PDEVICE_OBJECT DeviceObject, PIRP Irp);
extern VOID MsrSnapshotInit(void);
extern VOID MsrSnapshotStop(void);

/* 全域狀態 */
PDEVICE_OBJECT g_DeviceObject = NULL;
LARGE_INTEGER  g_CmCookie = {0};
PVOID          g_ObHandle = NULL;

/* ObRegisterCallbacks 需要 altitude 字串 */
static UNICODE_STRING ObAltitude = RTL_CONSTANT_STRING(L"321000");

/* 反載入 */
static VOID DriverUnload(PDRIVER_OBJECT DriverObject)
{
    UNICODE_STRING symLink = RTL_CONSTANT_STRING(L"\\DosDevices\\BlueSquadron");

    /* 停 MSR 快照定時器 */
    MsrSnapshotStop();

    /* 反註冊回呼 */
    PsRemoveLoadImageNotifyRoutine(OnImageLoad);
    PsSetCreateProcessNotifyRoutineEx(OnProcessNotify, TRUE);

    if (g_CmCookie.QuadPart != 0)
        CmUnRegisterCallback(g_CmCookie);

    if (g_ObHandle != NULL)
        ObUnRegisterCallbacks(g_ObHandle);

    /* 刪除裝置 */
    IoDeleteSymbolicLink(&symLink);
    if (g_DeviceObject != NULL)
        IoDeleteDevice(g_DeviceObject);
}

NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    NTSTATUS status;
    UNICODE_STRING devName  = RTL_CONSTANT_STRING(L"\\Device\\BlueSquadron");
    UNICODE_STRING symLink  = RTL_CONSTANT_STRING(L"\\DosDevices\\BlueSquadron");

    UNREFERENCED_PARAMETER(RegistryPath);
    DriverObject->DriverUnload = DriverUnload;

    /* 1. 建立裝置物件（限管理員存取） */
    /* 用 IoCreateDeviceSecure 而非 IoCreateDevice：沒有 SDDL 的話任何已登入使用者都能開
       \\.\/BlueSquadron 並操作驅動封鎖清單與行程保護——那是權限提升。
       D:P(A;;GA;;;BA) = 僅允許 Builtin Administrators 完整存取。 */
    UNICODE_STRING sddl = RTL_CONSTANT_STRING(L"D:P(A;;GA;;;BA)");
    UNICODE_STRING classGuid = RTL_CONSTANT_STRING(L"{4d36e97d-e325-11ce-bfc1-08002be10318}");
    status = IoCreateDeviceSecure(
        DriverObject, 0, &devName,
        FILE_DEVICE_UNKNOWN, FILE_DEVICE_SECURE_OPEN, FALSE,
        &sddl, &classGuid,
        &g_DeviceObject);
    if (!NT_SUCCESS(status)) return status;

    status = IoCreateSymbolicLink(&symLink, &devName);
    if (!NT_SUCCESS(status))
    {
        IoDeleteDevice(g_DeviceObject);
        return status;
    }

    /* 2. 設定 IOCTL 派發 */
    DriverObject->MajorFunction[IRP_MJ_CREATE]         = IoctlDispatch;
    DriverObject->MajorFunction[IRP_MJ_CLOSE]          = IoctlDispatch;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = IoctlDispatch;

    /* 3. 驅動/DLL 載入回呼 */
    status = PsSetLoadImageNotifyRoutine(OnImageLoad);
    if (!NT_SUCCESS(status))
        DbgPrint("[BS] PsSetLoadImageNotifyRoutine failed: 0x%08X\n", status);

    /* 4. 進程建立回呼 */
    status = PsSetCreateProcessNotifyRoutineEx(OnProcessNotify, FALSE);
    if (!NT_SUCCESS(status))
        DbgPrint("[BS] PsSetCreateProcessNotifyRoutineEx failed: 0x%08X\n", status);

    /* 5. 登錄檔回呼 */
    status = CmRegisterCallbackEx(OnRegistryCallback, &ObAltitude, DriverObject, NULL, &g_CmCookie, NULL);
    if (!NT_SUCCESS(status))
    {
        g_CmCookie.QuadPart = 0;
        DbgPrint("[BS] CmRegisterCallbackEx failed: 0x%08X\n", status);
    }

    /* 6. 物件存取回呼（保護 LSASS 等受保護進程） */
    {
        OB_OPERATION_REGISTRATION opReg = {0};
        OB_CALLBACK_REGISTRATION cbReg = {0};

        opReg.ObjectType = PsProcessType;
        opReg.Operations = OB_OPERATION_HANDLE_CREATE | OB_OPERATION_HANDLE_DUPLICATE;
        /* PreOperation 和 PostOperation 定義在 Callbacks.c */
        extern OB_PREOP_CALLBACK_STATUS OnObPreOp(PVOID, POB_PRE_OPERATION_INFORMATION);
        opReg.PreOperation = OnObPreOp;

        cbReg.Version                    = OB_FLT_REGISTRATION_VERSION;
        cbReg.OperationRegistrationCount = 1;
        cbReg.Altitude                   = ObAltitude;
        cbReg.RegistrationContext        = NULL;
        cbReg.OperationRegistration      = &opReg;

        status = ObRegisterCallbacks(&cbReg, &g_ObHandle);
        if (!NT_SUCCESS(status))
        {
            g_ObHandle = NULL;
            DbgPrint("[BS] ObRegisterCallbacks failed: 0x%08X\n", status);
        }
    }

    /* 7. MSR 定期快照 */
    MsrSnapshotInit();

    DbgPrint("[BS] Blue Squadron loaded.\n");
    return STATUS_SUCCESS;
}
