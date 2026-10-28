// ============================================================================
// ShouTech ERP — SecEng Enterprise Key Vault (in-process lifecycle)
// Generate → Active → Rotate → Retired / Revoked
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/keyvault_internal.h"
#include "../internal/crypto_internal.h"
#include "../internal/secure_memory.h"

#include <mutex>
#include <vector>
#include <cstring>
#include <chrono>

using shoutech::security::status;
namespace kv = shoutech::security::internal::keyvault;
namespace ic = shoutech::security::internal::crypto;
namespace sm = shoutech::security::internal;

namespace {

std::mutex g_mutex;
std::vector<sm::key_record> g_keys;

std::uint64_t now_unix() noexcept
{
    using namespace std::chrono;
    return static_cast<std::uint64_t>(
        duration_cast<seconds>(system_clock::now().time_since_epoch()).count());
}

void make_key_id(sm::key_purpose purpose, char out[48]) noexcept
{
    std::uint8_t rnd[8]{};
    ic::random_bytes(rnd);
    const char* p = "APP";
    switch (purpose)
    {
    case sm::key_purpose::license: p = "LIC"; break;
    case sm::key_purpose::backup: p = "BKP"; break;
    case sm::key_purpose::cloud: p = "CLD"; break;
    case sm::key_purpose::database: p = "DB"; break;
    case sm::key_purpose::recovery: p = "REC"; break;
    default: break;
    }
    std::snprintf(
        out,
        48,
        "ST-%s-%02X%02X%02X%02X-%02X%02X%02X%02X",
        p,
        rnd[0], rnd[1], rnd[2], rnd[3],
        rnd[4], rnd[5], rnd[6], rnd[7]);
    sm::secure_zero(rnd, sizeof(rnd));
}

} // anon

namespace shoutech::security::internal::keyvault {

status generate_key(key_purpose purpose, std::string_view scope, key_record& out) noexcept
{
    key_record rec{};
    rec.purpose = purpose;
    rec.created_at_unix = now_unix();
    rec.version = 1;
    rec.status = key_status::active;
    rec.key_len = 32;
    std::snprintf(rec.algorithm, sizeof(rec.algorithm), "AES-256-GCM");
    std::snprintf(rec.scope, sizeof(rec.scope), "%.31s", scope.empty() ? "local" : scope.data());
    make_key_id(purpose, rec.key_id);

    const status st = ic::random_bytes(std::span<std::uint8_t>(rec.key_material, rec.key_len));
    if (st != status::ok)
        return st;

    {
        std::lock_guard lock(g_mutex);
        g_keys.push_back(rec);
    }

    out = rec;
    // Do not leave key material in caller's copy longer than needed by caller policy.
    return status::ok;
}

status get_active_key(key_purpose purpose, std::string_view scope, key_record& out) noexcept
{
    std::lock_guard lock(g_mutex);
    for (auto it = g_keys.rbegin(); it != g_keys.rend(); ++it)
    {
        if (it->purpose == purpose &&
            it->status == key_status::active &&
            (scope.empty() || scope == it->scope))
        {
            out = *it;
            return status::ok;
        }
    }
    return status::not_initialized;
}

status rotate_key(std::string_view key_id, key_record& out_new) noexcept
{
    key_purpose purpose = key_purpose::application;
    char scope[32]{ "local" };
    {
        std::lock_guard lock(g_mutex);
        bool found = false;
        for (auto& k : g_keys)
        {
            if (key_id == k.key_id && k.status == key_status::active)
            {
                k.status = key_status::retired;
                purpose = k.purpose;
                std::snprintf(scope, sizeof(scope), "%s", k.scope);
                found = true;
                break;
            }
        }
        if (!found)
            return status::invalid_parameter;
    }
    return generate_key(purpose, scope, out_new);
}

status revoke_key(std::string_view key_id) noexcept
{
    std::lock_guard lock(g_mutex);
    for (auto& k : g_keys)
    {
        if (key_id == k.key_id)
        {
            k.status = key_status::revoked;
            sm::secure_zero(k.key_material, sizeof(k.key_material));
            k.key_len = 0;
            return status::ok;
        }
    }
    return status::invalid_parameter;
}

status wrap_dek(
    std::span<const std::uint8_t> dek,
    std::span<const std::uint8_t> kek,
    std::span<std::uint8_t> wrapped_out,
    std::uint32_t& wrapped_len,
    std::span<std::uint8_t, 12> nonce_out,
    std::span<std::uint8_t, 16> tag_out) noexcept
{
    if (dek.size() != 32 || kek.size() != 32)
        return status::crypto_invalid_key;
    if (wrapped_out.size() < dek.size())
        return status::buffer_too_small;

    status st = ic::random_bytes(nonce_out);
    if (st != status::ok)
        return st;

    return ic::aes256_gcm_encrypt(
        kek,
        nonce_out,
        {},
        dek,
        wrapped_out,
        wrapped_len,
        tag_out);
}

status unwrap_dek(
    std::span<const std::uint8_t> wrapped,
    std::span<const std::uint8_t> kek,
    std::span<const std::uint8_t> nonce,
    std::span<const std::uint8_t> tag,
    std::span<std::uint8_t> dek_out,
    std::uint32_t& dek_len) noexcept
{
    if (kek.size() != 32 || tag.size() != 16 || nonce.size() != 12)
        return status::invalid_parameter;

    std::uint8_t tag_arr[16]{};
    std::memcpy(tag_arr, tag.data(), 16);
    std::span<const std::uint8_t, 16> tag_span(tag_arr, 16);
    return ic::aes256_gcm_decrypt(kek, nonce, {}, wrapped, tag_span, dek_out, dek_len);
}

void clear_all() noexcept
{
    std::lock_guard lock(g_mutex);
    for (auto& k : g_keys)
        sm::secure_zero(k.key_material, sizeof(k.key_material));
    g_keys.clear();
}

} // namespace
