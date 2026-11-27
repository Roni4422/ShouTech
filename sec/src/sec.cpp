// ============================================================================
// ShouTech ERP — SecEng Engine Entry (orchestration)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"

// Primary ST_Initialize / ST_Shutdown remain in prot.cpp for compatibility.
// Keep the DLL entry point minimal: Windows loader-lock callbacks must not
// acquire application locks, stop threads, or perform other complex cleanup.

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(instance);
    return TRUE;
}
