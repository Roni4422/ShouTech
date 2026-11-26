// ============================================================================
// ShouTech ERP — SecEng Internal: Enterprise Key Vault
// ============================================================================
#pragma once

#include "sec_internal.h"
#include "secure_memory.h"
#include <optional>
#include <vector>
#include <string_view>

namespace shoutech::security::internal::keyvault {

[[nodiscard]] status generate_key(
    key_purpose purpose,
    std::string_view scope,
    key_record& out) noexcept;

[[nodiscard]] status get_active_key(
    key_purpose purpose,
    std::string_view scope,
    key_record& out) noexcept;

[[nodiscard]] status rotate_key(
    std::string_view key_id,
    key_record& out_new) noexcept;

[[nodiscard]] status revoke_key(std::string_view key_id) noexcept;

[[nodiscard]] status wrap_dek(
    std::span<const std::uint8_t> dek,
    std::span<const std::uint8_t> kek,
    std::span<std::uint8_t> wrapped_out,
    std::uint32_t& wrapped_len,
    std::span<std::uint8_t, 12> nonce_out,
    std::span<std::uint8_t, 16> tag_out) noexcept;

[[nodiscard]] status unwrap_dek(
    std::span<const std::uint8_t> wrapped,
    std::span<const std::uint8_t> kek,
    std::span<const std::uint8_t> nonce,
    std::span<const std::uint8_t> tag,
    std::span<std::uint8_t> dek_out,
    std::uint32_t& dek_len) noexcept;

void clear_all() noexcept;

} // namespace shoutech::security::internal::keyvault
