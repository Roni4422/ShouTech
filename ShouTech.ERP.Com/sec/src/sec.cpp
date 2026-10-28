// ============================================================================
// ShouTech ERP — SecEng Engine Entry (orchestration)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"
#include "../internal/keyvault_internal.h"

// Primary ST_Initialize / ST_Shutdown remain in prot.cpp for compatibility.
// This translation unit ensures modular object files are linked and provides
// process detach cleanup hooks for key material.

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        break;
    case DLL_PROCESS_DETACH:
        shoutech::security::internal::keyvault::clear_all();
        break;
    default:
        break;
    }
    return TRUE;
}
