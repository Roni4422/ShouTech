// ============================================================================
// ShouTech ERP — SecEng Internal: Crypto Core
// AES-256-GCM · SHA-2 · HMAC · PBKDF2 · CSPRNG via Windows CNG (BCrypt)
// ============================================================================
#pragma once

#include <Windows.h>
#include <bcrypt.h>

#include <cstdint>
#include <cstddef>
#include <span>
#include <optional>

#include "sec.h"
#include "secure_memory.h"

#pragma comment(lib, "bcrypt.lib")

namespace shoutech::security::internal::crypto {

inline constexpr std::size_t kAesKeyBytes = 32;
inline constexpr std::size_t kGcmNonceBytes = 12;
inline constexpr std::size_t kGcmTagBytes = 16;
inline constexpr std::size_t kSha256Bytes = 32;
inline constexpr std::size_t kSha512Bytes = 64;
inline constexpr std::uint32_t kPbkdf2MinIterations = 100'000;

[[nodiscard]] bool is_valid_aes256_key(std::span<const std::uint8_t> key) noexcept;
[[nodiscard]] bool is_valid_gcm_nonce(std::span<const std::uint8_t> nonce) noexcept;

[[nodiscard]] status random_bytes(std::span<std::uint8_t> out) noexcept;

[[nodiscard]] status sha256(
    std::span<const std::uint8_t> data,
    std::span<std::uint8_t, kSha256Bytes> out) noexcept;

[[nodiscard]] status sha512(
    std::span<const std::uint8_t> data,
    std::span<std::uint8_t, kSha512Bytes> out) noexcept;

[[nodiscard]] status hmac_sha256(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> data,
    std::span<std::uint8_t, kSha256Bytes> out) noexcept;

[[nodiscard]] status pbkdf2_sha256(
    std::span<const std::uint8_t> password,
    std::span<const std::uint8_t> salt,
    std::uint32_t iterations,
    std::span<std::uint8_t> out_key) noexcept;

struct gcm_encrypt_result
{
    std::uint32_t ciphertext_len{};
};

[[nodiscard]] status aes256_gcm_encrypt(
    std::span<const std::uint8_t> key,      // 32
    std::span<const std::uint8_t> nonce,    // 12
    std::span<const std::uint8_t> aad,      // optional
    std::span<const std::uint8_t> plaintext,
    std::span<std::uint8_t> ciphertext_out,
    std::uint32_t& ciphertext_len,
    std::span<std::uint8_t, kGcmTagBytes> tag_out) noexcept;

[[nodiscard]] status aes256_gcm_decrypt(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> nonce,
    std::span<const std::uint8_t> aad,
    std::span<const std::uint8_t> ciphertext,
    std::span<const std::uint8_t, kGcmTagBytes> tag,
    std::span<std::uint8_t> plaintext_out,
    std::uint32_t& plaintext_len) noexcept;

} // namespace shoutech::security::internal::crypto
