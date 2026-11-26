// ============================================================================
// ShouTech ERP — SecEng: CSPRNG (Windows CNG BCryptGenRandom)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/crypto_internal.h"
#include "../internal/sec_internal.h"

#include <bcrypt.h>

using shoutech::security::status;
using shoutech::security::internal::set_status;
namespace ic = shoutech::security::internal::crypto;

namespace shoutech::security::internal::crypto {

status random_bytes(std::span<std::uint8_t> out) noexcept
{
    if (out.empty() || out.data() == nullptr)
        return status::invalid_parameter;

    const NTSTATUS nt = BCryptGenRandom(
        nullptr,
        out.data(),
        static_cast<ULONG>(out.size()),
        BCRYPT_USE_SYSTEM_PREFERRED_RNG);

    if (!BCRYPT_SUCCESS(nt))
        return status::crypto_random;

    return status::ok;
}

} // namespace

// Public C API — also implemented in legacy prot.cpp; this module owns the
// shared internal implementation. Prefer linking one export site only.
// When modular build is active, crypto exports live in crypto.cpp / hashing.cpp.
