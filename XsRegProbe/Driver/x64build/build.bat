@echo off
rem Build XsRegProbe.sys without the WDK MSBuild toolset (not installed here).
rem INCLUDE: WDK km only (mixing UCRT/MSVC headers breaks km\crt).
rem LIB: WDK km + MSVC + UCRT (link needs LIBCMT).
rem Working dir is Driver\ (XsRegProbe.c lives there), not this x64build\ folder.
setlocal
cd /d "%~dp0.."
set MSVC=H:\a\VC\Tools\MSVC\14.51.36231
set WDK=C:\Program Files (x86)\Windows Kits\10
set INCLUDE=%WDK%\Include\10.0.28000.0\km;%WDK%\Include\10.0.28000.0\km\crt;%WDK%\Include\10.0.28000.0\shared
set LIB=%WDK%\Lib\10.0.28000.0\km\x64;%MSVC%\lib\x64;%WDK%\Lib\10.0.28000.0\ucrt\x64
if not exist "%MSVC%\bin\Hostx64\x64\cl.exe" exit /b 2
if not exist "%WDK%\Include\10.0.28000.0\km\ntddk.h" exit /b 3
if not exist x64build mkdir x64build
"%MSVC%\bin\Hostx64\x64\cl.exe" /nologo /c /O2 /GS- /kernel /D_AMD64_=1 /DAMD64 /DWIN32=100 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000000 /Fo:x64build\XsRegProbe.obj XsRegProbe.c
if errorlevel 1 exit /b 1
"%MSVC%\bin\Hostx64\x64\link.exe" /nologo /DRIVER /SUBSYSTEM:NATIVE /ENTRY:DriverEntry /OUT:x64build\XsRegProbe.sys x64build\XsRegProbe.obj wdm.lib wdmsec.lib ntoskrnl.lib BufferOverflowK.lib
if errorlevel 1 exit /b 1
echo BUILT: %~dp0XsRegProbe.sys
