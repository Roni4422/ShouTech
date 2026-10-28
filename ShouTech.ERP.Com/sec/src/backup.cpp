// ============================================================================
// ShouTech ERP — SecEng STBK2 Backup Security Envelope
// Random DEK → AES-256-GCM payload → DEK wrapped by KEK (KEK never stored)
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/backup_internal.h"
#include "../internal/keyvault_internal.h"
#include "../internal/secure_memory.h"

#include <chrono>
#include <cstring>

using shoutech::security::status;
namespace bk = shoutech::security::internal::backup;
namespace ic = shoutech::security::internal::crypto;
namespace sm = shoutech::security::internal;
namespace kv = shoutech::security::internal::keyvault;

namespace shoutech::security::internal::backup {

bool validate_header(const stbk2::header& header) noexcept
{
    if (header.magic != stbk2::magic)
        return false;
    if (header.format_version != stbk2::format_version)
        return false;
    if (header.algorithm != stbk2::encryption_algorithm::aes256_gcm)
        return false;
    if (header.wrapped_dek_len == 0 || header.wrapped_dek_len > sizeof(header.wrapped_dek))
        return false;
    return true;
}

status create_stbk2(
    const create_params& params,
    stbk2::header& out_header,
    std::span<std::uint8_t> ciphertext_out,
    std::uint32_t& ciphertext_len) noexcept
{
    ciphertext_len = 0;
    if (params.kek.size() != 32)
        return status::crypto_invalid_key;
    if (params.plaintext_payload.empty())
        return status::invalid_parameter;
    if (ciphertext_out.size() < params.plaintext_payload.size())
        return status::buffer_too_small;

    stbk2::header h{};
    h.magic = stbk2::magic;
    h.format_version = stbk2::format_version;
    h.header_size = stbk2::header_size;
    h.algorithm = stbk2::encryption_algorithm::aes256_gcm;
    h.created_at_unix = static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch())
            .count());

    if (params.company_id)
        std::memcpy(h.company_id, params.company_id, 16);

    if (params.provider)
        std::snprintf(h.provider, sizeof(h.provider), "%.31s", params.provider);

    if (params.key_id)
        std::snprintf(h.key_id, sizeof(h.key_id), "%.47s", params.key_id);

    // Random backup id + DEK
    status st = ic::random_bytes(std::span<std::uint8_t>(h.backup_id, 16));
    if (st != status::ok)
        return st;

    sm::secure_buffer<32> dek;
    st = ic::random_bytes(dek.span());
    if (st != status::ok)
        return st;

    st = ic::random_bytes(std::span<std::uint8_t>(h.payload_nonce, 12));
    if (st != status::ok)
        return st;

    st = ic::aes256_gcm_encrypt(
        dek.span(),
        std::span<const std::uint8_t>(h.payload_nonce, 12),
        params.metadata,
        params.plaintext_payload,
        ciphertext_out,
        ciphertext_len,
        std::span<std::uint8_t, 16>(h.payload_tag, 16));
    if (st != status::ok)
        return st;

    // Wrap DEK with KEK
    std::uint8_t wrap_nonce[12]{};
    std::uint8_t wrap_tag[16]{};
    std::uint32_t wrapped_len = 0;
    // Store: wrapped_dek = ciphertext(DEK) || we keep nonce/tag in reserved area
    // Layout in wrapped_dek: 12 nonce + 16 tag + 32 ct = 60 — our field is 48.
    // Simpler: wrap into temporary then store ct only; put nonce/tag in reserved.
    std::uint8_t wrapped_ct[32]{};
    st = kv::wrap_dek(
        dek.span(),
        params.kek,
        std::span<std::uint8_t>(wrapped_ct, 32),
        wrapped_len,
        std::span<std::uint8_t, 12>(wrap_nonce, 12),
        std::span<std::uint8_t, 16>(wrap_tag, 16));
    if (st != status::ok)
        return st;

    std::memcpy(h.wrapped_dek, wrapped_ct, 32);
    h.wrapped_dek_len = 32;
    // Pack wrap nonce+tag into reserved[0..27]
    std::memcpy(h.reserved, wrap_nonce, 12);
    std::memcpy(h.reserved + 12, wrap_tag, 16);

    h.metadata_len = static_cast<std::uint32_t>(params.metadata.size());
    h.payload_len = ciphertext_len;

    out_header = h;
    sm::secure_zero(wrap_nonce, sizeof(wrap_nonce));
    sm::secure_zero(wrap_tag, sizeof(wrap_tag));
    sm::secure_zero(wrapped_ct, sizeof(wrapped_ct));
    return status::ok;
}

status open_stbk2(
    const stbk2::header& header,
    std::span<const std::uint8_t> ciphertext,
    std::span<const std::uint8_t> kek,
    std::span<std::uint8_t> plaintext_out,
    std::uint32_t& plaintext_len) noexcept
{
    plaintext_len = 0;
    if (!validate_header(header))
        return status::invalid_parameter;
    if (kek.size() != 32)
        return status::crypto_invalid_key;

    std::uint8_t wrap_nonce[12]{};
    std::uint8_t wrap_tag[16]{};
    std::memcpy(wrap_nonce, header.reserved, 12);
    std::memcpy(wrap_tag, header.reserved + 12, 16);

    sm::secure_buffer<32> dek;
    std::uint32_t dek_len = 0;
    status st = kv::unwrap_dek(
        std::span<const std::uint8_t>(header.wrapped_dek, header.wrapped_dek_len),
        kek,
        std::span<const std::uint8_t>(wrap_nonce, 12),
        std::span<const std::uint8_t>(wrap_tag, 16),
        dek.span(),
        dek_len);
    sm::secure_zero(wrap_nonce, sizeof(wrap_nonce));
    sm::secure_zero(wrap_tag, sizeof(wrap_tag));
    if (st != status::ok)
        return st;

    std::uint8_t tag_copy[16]{};
    std::memcpy(tag_copy, header.payload_tag, 16);
    st = ic::aes256_gcm_decrypt(
        dek.span(),
        std::span<const std::uint8_t>(header.payload_nonce, 12),
        {},
        ciphertext,
        std::span<const std::uint8_t, 16>(tag_copy, 16),
        plaintext_out,
        plaintext_len);
    sm::secure_zero(tag_copy, sizeof(tag_copy));
    return st;
}

} // namespace

// ─── Public C API: STBK2 company backup envelope ───────────────────────────
// Wire format matches managed Stbk2Envelope / internal stbk2::header (256-byte head).

extern "C" {

ST_API ST_STATUS ST_CALL ST_CreateStbk2Backup(
    const std::uint8_t* kek32,
    const std::uint8_t* company_id16,
    const char* provider,
    const char* key_id,
    const std::uint8_t* metadata,
    std::uint32_t metadata_len,
    const std::uint8_t* plaintext,
    std::uint32_t plaintext_len,
    std::uint8_t* out_buffer,
    std::uint32_t out_buffer_size,
    std::uint32_t* out_len)
{
    using shoutech::security::status;
    namespace stbk2 = shoutech::security::internal::stbk2;
    namespace bk = shoutech::security::internal::backup;

    if (!kek32 || !plaintext || plaintext_len == 0 || !out_buffer || !out_len)
        return status::invalid_parameter;

    bk::create_params params{};
    params.company_id = company_id16;
    params.provider = provider ? provider : "local";
    params.key_id = key_id;
    params.metadata = std::span<const std::uint8_t>(metadata, metadata ? metadata_len : 0);
    params.plaintext_payload = std::span<const std::uint8_t>(plaintext, plaintext_len);
    params.kek = std::span<const std::uint8_t>(kek32, 32);

    stbk2::header header{};
    // Ciphertext buffer must be at least plaintext size; full file = 256 + meta + ct
    const std::uint32_t need = 256u + (metadata ? metadata_len : 0u) + plaintext_len;
    if (out_buffer_size < need)
        return status::buffer_too_small;

    std::uint8_t* ct = out_buffer + 256 + (metadata ? metadata_len : 0);
    std::uint32_t ct_len = 0;
    status st = bk::create_stbk2(
        params,
        header,
        std::span<std::uint8_t>(ct, plaintext_len),
        ct_len);
    if (st != status::ok)
        return st;

    // Write a compact 256-byte header image (compatible with managed Stbk2Envelope offsets).
    std::memset(out_buffer, 0, 256);
    std::memcpy(out_buffer + 0, &header.magic, 4);
    std::memcpy(out_buffer + 4, &header.format_version, 2);
    std::uint16_t hs = 256;
    std::memcpy(out_buffer + 6, &hs, 2);
    std::memcpy(out_buffer + 8, header.backup_id, 16);
    std::memcpy(out_buffer + 24, header.company_id, 16);
    std::memcpy(out_buffer + 40, header.provider, 32);
    std::memcpy(out_buffer + 72, &header.created_at_unix, 8);
    std::uint16_t algo = static_cast<std::uint16_t>(header.algorithm);
    std::memcpy(out_buffer + 80, &algo, 2);
    std::memcpy(out_buffer + 84, header.key_id, 48);
    std::memcpy(out_buffer + 132, header.wrapped_dek, 32);
    std::memcpy(out_buffer + 180, &header.wrapped_dek_len, 4);
    std::memcpy(out_buffer + 184, header.payload_nonce, 12);
    std::memcpy(out_buffer + 196, header.payload_tag, 16);
    std::uint32_t ml = metadata ? metadata_len : 0;
    std::memcpy(out_buffer + 212, &ml, 4);
    std::uint64_t pl = ct_len;
    std::memcpy(out_buffer + 216, &pl, 8);
    std::memcpy(out_buffer + 224, header.reserved, 28); // wrap nonce+tag

    if (metadata && metadata_len)
        std::memcpy(out_buffer + 256, metadata, metadata_len);

    *out_len = need;
    return status::ok;
}

ST_API ST_STATUS ST_CALL ST_ValidateStbk2Header(
    const std::uint8_t* file_data,
    std::uint32_t file_len)
{
    using shoutech::security::status;
    namespace stbk2 = shoutech::security::internal::stbk2;

    if (!file_data || file_len < 256)
        return status::invalid_parameter;
    std::uint32_t magic = 0;
    std::memcpy(&magic, file_data, 4);
    if (magic != stbk2::magic)
        return status::invalid_parameter;
    std::uint16_t ver = 0;
    std::memcpy(&ver, file_data + 4, 2);
    if (ver != stbk2::format_version)
        return status::unsupported_version;
    return status::ok;
}

} // extern "C"
