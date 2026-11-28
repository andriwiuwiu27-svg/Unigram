#pragma once

// Force-included into widget-sandbox sources that call Win32 the SDK declares for the desktop partition
// alone, although WindowsApp.lib exports it: the submodule stays unmodified.
#include <windows.h>

#if !WINAPI_FAMILY_PARTITION(WINAPI_PARTITION_DESKTOP)
extern "C" WINBASEAPI BOOL WINAPI QueryThreadCycleTime(HANDLE ThreadHandle, PULONG64 CycleTime);
#endif
