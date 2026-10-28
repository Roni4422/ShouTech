// ============================================================================
// ShouTech ERP — SecEng License Engine (module shell)
// Real signed validation binds to native provider; no fake-success stubs.
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"

namespace shoutech::security::internal::license {
// License parsing / signature verification modules expand here.
// Public ST_IsLicenseValid / ST_GetEdition remain bridged via prot.cpp until
// full signed license format is finalized.
}
