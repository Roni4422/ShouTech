// ============================================================================
// ShouTech ERP — SecEng Diagnostics (safe messages, no secret leakage)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"

#include <mutex>
#include <cstring>

namespace shoutech::security::internal {

engine_state& engine() noexcept
{
    static engine_state s;
    return s;
}

void set_status(status s) noexcept
{
    engine().last_status.store(s, std::memory_order_release);
}

void set_diagnostic(status s, const char* diagnostic_id, const wchar_t* safe_message) noexcept
{
    auto& e = engine();
    e.last_status.store(s, std::memory_order_release);
    std::lock_guard lock(e.mutex);
    if (diagnostic_id)
    {
        std::strncpy(e.last_diagnostic_id, diagnostic_id, sizeof(e.last_diagnostic_id) - 1);
        e.last_diagnostic_id[sizeof(e.last_diagnostic_id) - 1] = '\0';
    }
    if (safe_message)
        e.last_diagnostic = safe_message;
}

const char* status_category(status s) noexcept
{
    const auto v = static_cast<std::uint32_t>(s);
    if (v == 0)
        return "success";
    if ((v & 0xFFFF'0000u) == 0x0004'0000u)
        return "info";
    if (v >= 0x8004'0010u && v <= 0x8004'001Fu)
        return "dongle";
    if (v >= 0x8004'0020u && v <= 0x8004'002Fu)
        return "license";
    if (v >= 0x8004'0030u && v <= 0x8004'003Fu)
        return "security";
    if (v >= 0x8004'0040u && v <= 0x8004'004Fu)
        return "crypto";
    if (v >= 0x8004'0050u && v <= 0x8004'005Fu)
        return "dpapi";
    return "general";
}

const wchar_t* status_safe_message(status s) noexcept
{
    switch (s)
    {
    case status::ok: return L"Success.";
    case status::not_initialized: return L"Security engine is not initialized.";
    case status::already_initialized: return L"Security engine is already initialized.";
    case status::invalid_parameter: return L"Invalid parameter.";
    case status::buffer_too_small: return L"Buffer is too small.";
    case status::out_of_memory: return L"Out of memory.";
    case status::internal_error: return L"Internal security error.";
    case status::not_implemented: return L"Feature is not implemented in this build.";
    case status::access_denied: return L"Access denied.";
    case status::dongle_not_found: return L"Security dongle was not found.";
    case status::license_not_found: return L"License was not found.";
    case status::license_invalid: return L"License is invalid.";
    case status::license_expired: return L"License has expired.";
    case status::license_hwid_mismatch: return L"License is not valid for this device.";
    case status::session_invalid: return L"Security session is invalid.";
    case status::integrity_check_failed: return L"Integrity verification failed.";
    case status::crypto_invalid_key: return L"Invalid cryptographic key.";
    case status::crypto_invalid_iv: return L"Invalid initialization vector.";
    case status::crypto_auth_tag_failed: return L"Data integrity check failed.";
    case status::crypto_weak_key: return L"Cryptographic parameters are too weak.";
    case status::crypto_encrypt: return L"Encryption failed.";
    case status::crypto_decrypt: return L"Decryption failed.";
    case status::crypto_random: return L"Random generation failed.";
    case status::crypto_key_derivation: return L"Key derivation failed.";
    case status::dpapi_protect: return L"Secure storage protect failed.";
    case status::dpapi_unprotect: return L"Secure storage unprotect failed.";
    default: return L"Security operation failed.";
    }
}

} // namespace
