// ============================================================================
// ShouTech ERP — SecEng DPAPI (Windows Data Protection API)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"
#include "../internal/secure_memory.h"

#include <wincrypt.h>
#pragma comment(lib, "crypt32.lib")

using shoutech::security::status;
using shoutech::security::internal::set_status;
using shoutech::security::internal::set_diagnostic;

extern "C" {

ST_API ST_STATUS ST_CALL ST_ProtectData(
    const std::uint8_t* plaintext,
    std::uint32_t plaintext_len,
    const std::uint8_t* entropy,
    std::uint32_t entropy_len,
    bool machine_scope,
    std::uint8_t* protected_data,
    std::uint32_t protected_buffer_size,
    std::uint32_t* protected_len)
{
    if (plaintext == nullptr || plaintext_len == 0 || protected_len == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }

    DATA_BLOB in{};
    in.pbData = const_cast<BYTE*>(plaintext);
    in.cbData = plaintext_len;

    DATA_BLOB ent{};
    if (entropy != nullptr && entropy_len > 0)
    {
        ent.pbData = const_cast<BYTE*>(entropy);
        ent.cbData = entropy_len;
    }

    DATA_BLOB out{};
    const DWORD flags = machine_scope ? CRYPTPROTECT_LOCAL_MACHINE : 0;
    if (!CryptProtectData(
            &in,
            L"ShouTech.SecEng",
            (entropy != nullptr && entropy_len > 0) ? &ent : nullptr,
            nullptr,
            nullptr,
            flags,
            &out))
    {
        set_status(status::dpapi_protect);
        set_diagnostic(status::dpapi_protect, "dpapi_protect_failed", L"Secure storage protect failed.");
        return status::dpapi_protect;
    }

    if (protected_data == nullptr || protected_buffer_size < out.cbData)
    {
        *protected_len = out.cbData;
        if (out.pbData)
            LocalFree(out.pbData);
        set_status(status::buffer_too_small);
        return status::buffer_too_small;
    }

    std::memcpy(protected_data, out.pbData, out.cbData);
    *protected_len = out.cbData;
    if (out.pbData)
        LocalFree(out.pbData);

    set_status(status::ok);
    return status::ok;
}

ST_API ST_STATUS ST_CALL ST_UnprotectData(
    const std::uint8_t* protected_data,
    std::uint32_t protected_len,
    const std::uint8_t* entropy,
    std::uint32_t entropy_len,
    std::uint8_t* plaintext,
    std::uint32_t plaintext_buffer_size,
    std::uint32_t* plaintext_len)
{
    if (protected_data == nullptr || protected_len == 0 || plaintext_len == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }

    DATA_BLOB in{};
    in.pbData = const_cast<BYTE*>(protected_data);
    in.cbData = protected_len;

    DATA_BLOB ent{};
    if (entropy != nullptr && entropy_len > 0)
    {
        ent.pbData = const_cast<BYTE*>(entropy);
        ent.cbData = entropy_len;
    }

    DATA_BLOB out{};
    if (!CryptUnprotectData(
            &in,
            nullptr,
            (entropy != nullptr && entropy_len > 0) ? &ent : nullptr,
            nullptr,
            nullptr,
            0,
            &out))
    {
        set_status(status::dpapi_unprotect);
        set_diagnostic(status::dpapi_unprotect, "dpapi_unprotect_failed", L"Secure storage unprotect failed.");
        return status::dpapi_unprotect;
    }

    if (plaintext == nullptr || plaintext_buffer_size < out.cbData)
    {
        *plaintext_len = out.cbData;
        if (out.pbData)
        {
            shoutech::security::internal::secure_zero(out.pbData, out.cbData);
            LocalFree(out.pbData);
        }
        set_status(status::buffer_too_small);
        return status::buffer_too_small;
    }

    std::memcpy(plaintext, out.pbData, out.cbData);
    *plaintext_len = out.cbData;
    if (out.pbData)
    {
        shoutech::security::internal::secure_zero(out.pbData, out.cbData);
        LocalFree(out.pbData);
    }

    set_status(status::ok);
    return status::ok;
}

} // extern "C"
