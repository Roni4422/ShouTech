// ============================================================================
// ShouTech ERP — SecEng Internal: STBK2 Backup Security
// ============================================================================
#pragma once

#include "sec_internal.h"
#include "crypto_internal.h"
#include <span>
#include <string_view>

namespace shoutech::security::internal::backup {

struct create_params
{
    const std::uint8_t* company_id{}; // 16 bytes GUID
    const char* provider{ "local" };
    const char* key_id{};
    std::span<const std::uint8_t> metadata;
    std::span<const std::uint8_t> plaintext_payload;
    std::span<const std::uint8_t> kek; // Key Encryption Key (never stored in file)
};

struct create_result
{
    stbk2::header header{};
    // ciphertext follows externally; caller allocates buffer = plaintext size
};

[[nodiscard]] status create_stbk2(
    const create_params& params,
    stbk2::header& out_header,
    std::span<std::uint8_t> ciphertext_out,
    std::uint32_t& ciphertext_len) noexcept;

[[nodiscard]] status open_stbk2(
    const stbk2::header& header,
    std::span<const std::uint8_t> ciphertext,
    std::span<const std::uint8_t> kek,
    std::span<std::uint8_t> plaintext_out,
    std::uint32_t& plaintext_len) noexcept;

[[nodiscard]] bool validate_header(const stbk2::header& header) noexcept;

} // namespace shoutech::security::internal::backup
