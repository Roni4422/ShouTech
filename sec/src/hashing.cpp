// ============================================================================
// ShouTech ERP — SecEng Hashing (SHA-256/512 · HMAC-SHA256 via CNG)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/crypto_internal.h"
#include "../internal/sec_internal.h"

#include <bcrypt.h>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

using shoutech::security::status;
using shoutech::security::internal::set_status;
namespace ic = shoutech::security::internal::crypto;

namespace shoutech::security::internal::crypto {

namespace {

status hash_with_alg(
    LPCWSTR alg_id,
    std::span<const std::uint8_t> data,
    std::span<std::uint8_t> out) noexcept
{
    if (out.data() == nullptr || out.empty())
        return status::invalid_parameter;

    BCRYPT_ALG_HANDLE hAlg = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    status result = status::crypto_hash;

    do
    {
        NTSTATUS nt = BCryptOpenAlgorithmProvider(&hAlg, alg_id, nullptr, 0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        DWORD hashObjectSize = 0, cbData = 0;
        nt = BCryptGetProperty(
            hAlg,
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&hashObjectSize),
            sizeof(hashObjectSize),
            &cbData,
            0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        std::vector<std::uint8_t> hashObject(hashObjectSize);
        nt = BCryptCreateHash(
            hAlg,
            &hHash,
            hashObject.data(),
            hashObjectSize,
            nullptr,
            0,
            0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        if (!data.empty())
        {
            nt = BCryptHashData(
                hHash,
                const_cast<PUCHAR>(data.data()),
                static_cast<ULONG>(data.size()),
                0);
            if (!BCRYPT_SUCCESS(nt))
                break;
        }

        nt = BCryptFinishHash(hHash, out.data(), static_cast<ULONG>(out.size()), 0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        result = status::ok;
    } while (false);

    if (hHash)
        BCryptDestroyHash(hHash);
    if (hAlg)
        BCryptCloseAlgorithmProvider(hAlg, 0);

    return result;
}

} // anon

status sha256(std::span<const std::uint8_t> data, std::span<std::uint8_t, kSha256Bytes> out) noexcept
{
    return hash_with_alg(BCRYPT_SHA256_ALGORITHM, data, out);
}

status sha512(std::span<const std::uint8_t> data, std::span<std::uint8_t, kSha512Bytes> out) noexcept
{
    return hash_with_alg(BCRYPT_SHA512_ALGORITHM, data, out);
}

status hmac_sha256(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> data,
    std::span<std::uint8_t, kSha256Bytes> out) noexcept
{
    if (key.empty() || key.data() == nullptr || out.data() == nullptr)
        return status::invalid_parameter;

    BCRYPT_ALG_HANDLE hAlg = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    status result = status::crypto_hash;

    do
    {
        NTSTATUS nt = BCryptOpenAlgorithmProvider(
            &hAlg,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            BCRYPT_ALG_HANDLE_HMAC_FLAG);
        if (!BCRYPT_SUCCESS(nt))
            break;

        DWORD hashObjectSize = 0, cbData = 0;
        nt = BCryptGetProperty(
            hAlg,
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&hashObjectSize),
            sizeof(hashObjectSize),
            &cbData,
            0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        std::vector<std::uint8_t> hashObject(hashObjectSize);
        nt = BCryptCreateHash(
            hAlg,
            &hHash,
            hashObject.data(),
            hashObjectSize,
            const_cast<PUCHAR>(key.data()),
            static_cast<ULONG>(key.size()),
            0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        if (!data.empty())
        {
            nt = BCryptHashData(
                hHash,
                const_cast<PUCHAR>(data.data()),
                static_cast<ULONG>(data.size()),
                0);
            if (!BCRYPT_SUCCESS(nt))
                break;
        }

        nt = BCryptFinishHash(hHash, out.data(), static_cast<ULONG>(out.size()), 0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        result = status::ok;
    } while (false);

    if (hHash)
        BCryptDestroyHash(hHash);
    if (hAlg)
        BCryptCloseAlgorithmProvider(hAlg, 0);

    return result;
}

status pbkdf2_sha256(
    std::span<const std::uint8_t> password,
    std::span<const std::uint8_t> salt,
    std::uint32_t iterations,
    std::span<std::uint8_t> out_key) noexcept
{
    if (password.empty() || salt.size() < 16 || out_key.empty())
        return status::invalid_parameter;
    if (iterations < kPbkdf2MinIterations)
        return status::crypto_weak_key;

    BCRYPT_ALG_HANDLE hAlg = nullptr;
    status result = status::crypto_key_derivation;

    do
    {
        NTSTATUS nt = BCryptOpenAlgorithmProvider(
            &hAlg,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            BCRYPT_ALG_HANDLE_HMAC_FLAG);
        if (!BCRYPT_SUCCESS(nt))
            break;

        nt = BCryptDeriveKeyPBKDF2(
            hAlg,
            const_cast<PUCHAR>(password.data()),
            static_cast<ULONG>(password.size()),
            const_cast<PUCHAR>(salt.data()),
            static_cast<ULONG>(salt.size()),
            iterations,
            out_key.data(),
            static_cast<ULONG>(out_key.size()),
            0);
        if (!BCRYPT_SUCCESS(nt))
            break;

        result = status::ok;
    } while (false);

    if (hAlg)
        BCryptCloseAlgorithmProvider(hAlg, 0);

    return result;
}

} // namespace

extern "C" {

ST_API ST_STATUS ST_CALL ST_HashSHA512(
    const std::uint8_t* data,
    std::uint32_t data_len,
    std::uint8_t* hash)
{
    if (hash == nullptr)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }

    std::span<const std::uint8_t> input(
        data == nullptr ? std::span<const std::uint8_t>{}
                        : std::span<const std::uint8_t>{ data, data_len });
    std::span<std::uint8_t, ic::kSha512Bytes> out(hash, ic::kSha512Bytes);
    const status st = ic::sha512(input, out);
    set_status(st);
    return st;
}

ST_API ST_STATUS ST_CALL ST_HMAC_SHA256(
    const std::uint8_t* key,
    std::uint32_t key_len,
    const std::uint8_t* data,
    std::uint32_t data_len,
    std::uint8_t* mac)
{
    if (key == nullptr || mac == nullptr || key_len == 0)
    {
        set_status(status::invalid_parameter);
        return status::invalid_parameter;
    }

    std::span<const std::uint8_t> k(key, key_len);
    std::span<const std::uint8_t> d(
        data == nullptr ? std::span<const std::uint8_t>{}
                        : std::span<const std::uint8_t>{ data, data_len });
    std::span<std::uint8_t, ic::kSha256Bytes> out(mac, ic::kSha256Bytes);
    const status st = ic::hmac_sha256(k, d, out);
    set_status(st);
    return st;
}

} // extern "C"
