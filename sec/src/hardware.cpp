// ============================================================================
// ShouTech ERP — SecEng Hardware Identity (HWID)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"

namespace shoutech::security::internal::hardware {
// Machine identity collection lives primarily in prot.cpp; this module is the
// future isolation point for HWID algorithms without mixing license logic.
}
