// ============================================================================
// ShouTech ERP — SecEng License Engine (module shell)
// Real signed validation binds to native provider; no fake-success stubs.
// ============================================================================
#include "sec.h"
#include "../internal/sec_internal.h"

namespace shoutech::security::internal::license {
// License parsing / signature verification modules expand here.
// Public ST_IsLicenseValid / ST_GetEdition remain bridged via prot.cpp until
// full signed license format is finalized.
}
