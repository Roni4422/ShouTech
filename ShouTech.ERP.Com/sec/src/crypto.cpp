// ============================================================================
// ShouTech ERP — SecEng Crypto Core
// AES-256-GCM · key validation · public encrypt/decrypt API
// Windows CNG (BCrypt) — no hand-rolled block ciphers
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/crypto_internal.h"
#include "../internal/sec_internal.h"
#include "../internal/secure_memory.h"

#include <bcrypt.h>
#include <vector>
#include <algorithm>

#pragma comment(lib, "bcrypt.lib")

using shoutech::security::status;
using shoutech::security::crypto_params;
using shoutech::security::internal::set_status;
using shoutech::security::internal::set_diagnostic;
namespace ic = shoutech::security::internal::crypto;
namespace sm = shoutech::security::internal;

namespace shoutech::security::internal::crypto {

bool is_valid_aes256_key(std::span<const std::uint8_t> key) noexcept
{
    if (key.size() != kAesKeyBytes || key.data() == nullptr)
        return false;

    // Reject all-zero key (explicit weak key policy).
    std::uint8_t acc = 0;
    for (auto b : key)
        acc = static_cast<std::uint8_t>(acc | b);
    return acc != 0;
}

bool is_valid_gcm_nonce(std::span<const std::uint8_t> nonce) noexcept
{
    return nonce.size() == kGcmNonceBytes && nonce.data() != nullptr;
}

namespace {

struct alg_handle
{
    BCRYPT_ALG_HANDLE h{ nullptr };
    ~alg_handle()
    {
        if (h)
            BCryptCloseAlgorithmProvider(h, 0);
    }
};

struct key_handle
{
    BCRYPT_KEY_HANDLE h{ nullptr };
    ~key_handle()
    {
        if (h)
            BCryptDestroyKey(h);
    }
};

status open_aes_gcm(alg_handle& alg) noexcept
{
    NTSTATUS nt = BCryptOpenAlgorithmProvider(&alg.h, BCRYPT_AES_ALGORITHM, nullptr, 0);
    if (!BCRYPT_SUCCESS(nt))
        return status::crypto_init;

    nt = BCryptSetProperty(
        alg.h,
        BCRYPT_CHAINING_MODE,
        reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_GCM)),
        static_cast<ULONG>((wcslen(BCRYPT_CHAIN_MODE_GCM) + 1) * sizeof(wchar_t)),
        0);

    if (!BCRYPT_SUCCESS(nt))
        return status::crypto_init;

    return status::ok;
}

} // anon

status aes256_gcm_encrypt(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> nonce,
    std::span<const std::uint8_t> aad,
    std::span<const std::uint8_t> plaintext,
    std::span<std::uint8_t> ciphertext_out,
    std::uint32_t& ciphertext_len,
    std::span<std::uint8_t, kGcmTagBytes> tag_out) noexcept
{
    ciphertext_len = 0;

    if (!is_valid_aes256_key(key))
        return status::crypto_invalid_key;
    if (!is_valid_gcm_nonce(nonce))
        return status::crypto_invalid_iv;
    if (plaintext.data() == nullptr && !plaintext.empty())
        return status::invalid_parameter;
    if (ciphertext_out.size() < plaintext.size())
        return status::buffer_too_small;
    if (tag_out.data() == nullptr)
        return status::invalid_parameter;

    alg_handle alg;
    status st = open_aes_gcm(alg);
    if (st != status::ok)
        return st;

    key_handle keyh;
    NTSTATUS nt = BCryptGenerateSymmetricKey(
        alg.h,
        &keyh.h,
        nullptr,
        0,
        const_cast<PUCHAR>(key.data()),
        static_cast<ULONG>(key.size()),
        0);
    if (!BCRYPT_SUCCESS(nt))
        return status::crypto_invalid_key;

    BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
    BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
    authInfo.pbNonce = const_cast<PUCHAR>(nonce.data());
    authInfo.cbNonce = static_cast<ULONG>(nonce.size());
    authInfo.pbTag = tag_out.data();
    authInfo.cbTag = static_cast<ULONG>(tag_out.size());
    if (!aad.empty())
    {
        authInfo.pbAuthData = const_cast<PUCHAR>(aad.data());
        authInfo.cbAuthData = static_cast<ULONG>(aad.size());
    }

    ULONG cbResult = 0;
    nt = BCryptEncrypt(
        keyh.h,
        plaintext.empty() ? nullptr : const_cast<PUCHAR>(plaintext.data()),
        static_cast<ULONG>(plaintext.size()),
        &authInfo,
        nullptr,
        0,
        ciphertext_out.empty() ? nullptr : ciphertext_out.data(),
        static_cast<ULONG>(ciphertext_out.size()),
        &cbResult,
        0);

    if (!BCRYPT_SUCCESS(nt))
        return status::crypto_encrypt;

    ciphertext_len = cbResult;
    return status::ok;
}

status aes256_gcm_decrypt(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> nonce,
    std::span<const std::uint8_t> aad,
    std::span<const std::uint8_t> ciphertext,
    std::span<const std::uint8_t, kGcmTagBytes> tag,
    std::span<std::uint8_t> plaintext_out,
    std::uint32_t& plaintext_len) noexcept
{
    plaintext_len = 0;

    if (!is_valid_aes256_key(key))
        return status::crypto_invalid_key;
    if (!is_valid_gcm_nonce(nonce))
        return status::crypto_invalid_iv;
    if (tag.data() == nullptr)
        return status::invalid_parameter;
    if (plaintext_out.size() < ciphertext.size())
        return status::buffer_too_small;

    alg_handle alg;
    status st = open_aes_gcm(alg);
    if (st != status::ok)
        return st;

    key_handle keyh;
    NTSTATUS nt = BCryptGenerateSymmetricKey(
        alg.h,
        &keyh.h,
        nullptr,
        0,
        const_cast<PUCHAR>(key.data()),
        static_cast<ULONG>(key.size()),
        0);
    if (!BCRYPT_SUCCESS(nt))
        return status::crypto_invalid_key;

    // Mutable tag copy required by BCrypt
    std::uint8_t tag_buf[kGcmTagBytes]{};
    std::memcpy(tag_buf, tag.data(), kGcmTagBytes);

    BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
    BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
    authInfo.pbNonce = const_cast<PUCHAR>(nonce.data());
    authInfo.cbNonce = static_cast<ULONG>(nonce.size());
    authInfo.pbTag = tag_buf;
    authInfo.cbTag = static_cast<ULONG>(kGcmTagBytes);
    if (!aad.empty())
    {
        authInfo.pbAuthData = const_cast<PUCHAR>(aad.data());
        authInfo.cbAuthData = static_cast<ULONG>(aad.size());
    }

    ULONG cbResult = 0;
    nt = BCryptDecrypt(
        keyh.h,
        ciphertext.empty() ? nullptr : const_cast<PUCHAR>(ciphertext.data()),
        static_cast<ULONG>(ciphertext.size()),
        &authInfo,
        nullptr,
        0,
        plaintext_out.empty() ? nullptr : plaintext_out.data(),
        static_cast<ULONG>(plaintext_out.size()),
        &cbResult,
        0);

    sm::secure_zero(tag_buf, sizeof(tag_buf));

    if (!BCRYPT_SUCCESS(nt))
    {
        // Authentication failure — do not distinguish beyond auth tag failed.
        return status::crypto_auth_tag_failed;
    }

    plaintext_len = cbResult;
    return status::ok;
}

} // namespace shoutech::security::internal::crypto

// ============================================================================
// Public C API — AES-256-GCM (not previously exported from prot.cpp)
// ============================================================================

extern "C" {

ST_API ST_STATUS ST_CALL ST_EncryptAES256GCM(
    const ST_CRYPTO_PARAMS* params,
    const std::uint8_t* plaintext,
    std::uint32_t plaintext_len,
    std::uint8_t* ciphertext,
    std::uint32_t ciphertext_buffer_size,
    std::uint32_t* ciphertext_len,
    std::uint8_t* tag)
{
    if (params == nullptr || ciphertext_len == nullptr || tag == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }
    if (params->key == nullptr || params->iv == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }

    std::span<const std::uint8_t> key(params->key, params->key_len);
    std::span<const std::uint8_t> nonce(params->iv, params->iv_len);
    std::span<const std::uint8_t> aad(
        params->aad == nullptr ? std::span<const std::uint8_t>{}
                               : std::span<const std::uint8_t>{ params->aad, params->aad_len });
    std::span<const std::uint8_t> pt(
        plaintext == nullptr ? std::span<const std::uint8_t>{}
                             : std::span<const std::uint8_t>{ plaintext, plaintext_len });
    std::span<std::uint8_t> ct(ciphertext, ciphertext_buffer_size);
    std::span<std::uint8_t, ic::kGcmTagBytes> tag_span(tag, ic::kGcmTagBytes);

    std::uint32_t out_len = 0;
    const status st = ic::aes256_gcm_encrypt(key, nonce, aad, pt, ct, out_len, tag_span);
    *ciphertext_len = out_len;
    set_status(st);
    if (st != status::ok)
        set_diagnostic(st, "crypto_encrypt_failed", L"Encryption failed.");
    return st;
}

ST_API ST_STATUS ST_CALL ST_DecryptAES256GCM(
    const ST_CRYPTO_PARAMS* params,
    const std::uint8_t* ciphertext,
    std::uint32_t ciphertext_len,
    const std::uint8_t* tag,
    std::uint8_t* plaintext,
    std::uint32_t plaintext_buffer_size,
    std::uint32_t* plaintext_len)
{
    if (params == nullptr || plaintext_len == nullptr || tag == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }
    if (params->key == nullptr || params->iv == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }

    std::span<const std::uint8_t> key(params->key, params->key_len);
    std::span<const std::uint8_t> nonce(params->iv, params->iv_len);
    std::span<const std::uint8_t> aad(
        params->aad == nullptr ? std::span<const std::uint8_t>{}
                               : std::span<const std::uint8_t>{ params->aad, params->aad_len });
    std::span<const std::uint8_t> ct(
        ciphertext == nullptr ? std::span<const std::uint8_t>{}
                              : std::span<const std::uint8_t>{ ciphertext, ciphertext_len });
    std::span<const std::uint8_t, ic::kGcmTagBytes> tag_span(tag, ic::kGcmTagBytes);
    std::span<std::uint8_t> pt(plaintext, plaintext_buffer_size);

    std::uint32_t out_len = 0;
    const status st = ic::aes256_gcm_decrypt(key, nonce, aad, ct, tag_span, pt, out_len);
    *plaintext_len = out_len;
    set_status(st);
    if (st == status::crypto_auth_tag_failed)
        set_diagnostic(st, "crypto_auth_tag_failed", L"Data integrity check failed.");
    return st;
}

} // extern "C"
