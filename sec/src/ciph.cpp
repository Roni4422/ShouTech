// ============================================================================
// ShouTech ERP — SecEng legacy cipher bridge
// AES-GCM / hashing / DPAPI live in crypto.cpp, hashing.cpp, dpapi.cpp.
// This file remains for build-path compatibility with older project files.
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"

// Intentionally empty: cryptographic implementations moved to modular sources.
