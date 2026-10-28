// ═══════════════════════════════════════════════════════════════════════════
// SHOUTECH ERP V9 - SECURITY ENGINE
// ═══════════════════════════════════════════════════════════════════════════
// FILE:        sec/inc/sec.h
// VERSION:     9.6.0 LTS
// LANGUAGE:    ISO C++20
// PLATFORM:    Windows x64 (10.0.19041+)
// COMPILER:    MSVC 19.30+, Clang 14+, GCC 11+
// OUTPUT:      SecEng.dll
// ═══════════════════════════════════════════════════════════════════════════
// COPYRIGHT (c) 2025 SHOUTECH. All rights reserved.
// 
// Licensed under commercial agreement. Unauthorized use prohibited.
// 
// Contact:
// - Licensing: license@shoutech.com
// - Security: security@shoutech.com
// - Support: support@shoutech.com
// ═══════════════════════════════════════════════════════════════════════════
// SECURITY DISCLOSURE:
// 
// Cryptographic algorithms implemented:
// - AES-256-GCM (NIST SP 800-38D)
// - PBKDF2-SHA256 (NIST SP 800-132)
// - SHA-256/512 (FIPS 180-4)
// - RSA-2048 digital signatures (FIPS 186-5)
// 
// Security audit status:
// - Internal review: 2025-01-20 (passed)
// - External audit: Scheduled Q2 2025
// - Penetration testing: Planned Q2 2025
// 
// Known limitations:
// - Side-channel resistance: Not formally verified (constant-time where critical)
// - Quantum resistance: Classical cryptography only (PQC planned v10.0)
// - Windows-only: Cross-platform support planned
// 
// Threat model:
// - Protects against: Software attacks, basic reverse engineering, license fraud
// - Does NOT protect against: Physical attacks, advanced nation-state attackers,
//   compromised OS kernel, hardware tampering without dongle
// ═══════════════════════════════════════════════════════════════════════════

#ifndef SHOUTECH_SEC_H_9DA8F3E1_4B72_4C9A_B8E5_2F7A3D1C0E96_
#define SHOUTECH_SEC_H_9DA8F3E1_4B72_4C9A_B8E5_2F7A3D1C0E96_

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 1: COMPILER & PLATFORM VALIDATION
// ═══════════════════════════════════════════════════════════════════════════

#if !defined(_M_AMD64) && !defined(_M_X64) && !defined(__x86_64__) && !defined(__amd64__)
    #error "SHOUTECH Security Engine requires x64 architecture"
#endif

#if !defined(_WIN32) && !defined(_WIN64)
    #error "SHOUTECH Security Engine currently targets Windows only"
#endif

#if defined(_MSC_VER)
    #if _MSC_VER < 1930
        #error "Requires MSVC 19.30+ (Visual Studio 2022 or later)"
    #endif
    #if defined(_MSVC_LANG) && _MSVC_LANG < 202002L
        #error "Requires C++20 mode (/std:c++20 or /std:c++latest)"
    #endif
#elif defined(__clang__)
    #if __clang_major__ < 14
        #error "Requires Clang 14 or later"
    #endif
    #if __cplusplus < 202002L
        #error "Requires C++20 mode (-std=c++20)"
    #endif
#elif defined(__GNUC__)
    #if __GNUC__ < 11
        #error "Requires GCC 11 or later"
    #endif
    #if __cplusplus < 202002L
        #error "Requires C++20 mode (-std=c++20)"
    #endif
#else
    #error "Unsupported compiler"
#endif

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 2: STANDARD INCLUDES
// ═══════════════════════════════════════════════════════════════════════════

#ifndef WINVER
    #define WINVER 0x0A00
#endif
#ifndef _WIN32_WINNT
    #define _WIN32_WINNT 0x0A00
#endif
#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
    #define NOMINMAX
#endif
#ifndef UNICODE
    #define UNICODE
#endif
#ifndef _UNICODE
    #define _UNICODE
#endif

#include <Windows.h>
#include <sal.h>

#include <cstdint>
#include <cstddef>
#include <span>
#include <string_view>
#include <array>
#include <type_traits>
#include <atomic>
#include <cstring>

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 3: API LINKAGE
// ═══════════════════════════════════════════════════════════════════════════

#if defined(ST_SEC_BUILDING_DLL)
    #define ST_API extern "C" __declspec(dllexport)
    #define ST_CXX_API __declspec(dllexport)
#elif defined(ST_SEC_STATIC_LIB)
    #define ST_API extern "C"
    #define ST_CXX_API
#else
    #define ST_API extern "C" __declspec(dllimport)
    #define ST_CXX_API __declspec(dllimport)
#endif

#define ST_CALL __stdcall

// Attributes
#define ST_DEPRECATED [[deprecated]]
#define ST_NODISCARD [[nodiscard]]
#define ST_NORETURN [[noreturn]]
#define ST_FALLTHROUGH [[fallthrough]]

// SAL annotations
#define ST_IN _In_
#define ST_OUT _Out_
#define ST_INOUT _Inout_
#define ST_OPT _In_opt_
#define ST_OUT_OPT _Out_opt_
#define ST_BUFFER(size) _In_reads_bytes_(size)
#define ST_OUT_BUFFER(size) _Out_writes_bytes_(size)

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 4: NAMESPACE & TYPES
// ═══════════════════════════════════════════════════════════════════════════

namespace shoutech::security {

// ───────────────────────────────────────────────────────────────────────────
// Version Information
// ───────────────────────────────────────────────────────────────────────────
inline constexpr std::uint32_t version_major = 9;
inline constexpr std::uint32_t version_minor = 6;
inline constexpr std::uint32_t version_patch = 0;
inline constexpr std::uint32_t version_build = 1;

inline constexpr std::uint32_t version_number = 
    (version_major << 24) | 
    (version_minor << 16) | 
    (version_patch << 8) | 
    version_build;

inline constexpr std::string_view version_string = "9.6.0.1-LTS";
inline constexpr std::string_view version_suffix = "LTS";

inline constexpr std::uint32_t api_version = 1;
inline constexpr std::uint32_t min_client_version = version_number;

// ───────────────────────────────────────────────────────────────────────────
// Cryptographic Constants (NIST/OWASP recommendations)
// ───────────────────────────────────────────────────────────────────────────
namespace crypto {
    // AES-256-GCM (NIST SP 800-38D)
    inline constexpr std::size_t aes_key_size = 32;
    inline constexpr std::size_t aes_block_size = 16;
    inline constexpr std::size_t aes_iv_size = 12;
    inline constexpr std::size_t aes_tag_size = 16;
    
    // RSA (FIPS 186-5)
    inline constexpr std::size_t rsa_key_bits = 2048;
    inline constexpr std::size_t rsa_key_bytes = 256;
    inline constexpr std::size_t rsa_signature_size = 256;
    
    // SHA (FIPS 180-4)
    inline constexpr std::size_t sha256_size = 32;
    inline constexpr std::size_t sha384_size = 48;
    inline constexpr std::size_t sha512_size = 64;
    
    // HMAC
    inline constexpr std::size_t hmac_sha256_size = 32;
    inline constexpr std::size_t hmac_sha512_size = 64;
    
    // PBKDF2 (NIST SP 800-132, OWASP 2024)
    inline constexpr std::uint32_t pbkdf2_min_iterations = 100'000;
    inline constexpr std::uint32_t pbkdf2_recommended_iterations = 600'000;
    inline constexpr std::uint32_t pbkdf2_paranoid_iterations = 1'000'000;
    
    // Salt
    inline constexpr std::size_t salt_min_size = 16;
    inline constexpr std::size_t salt_recommended_size = 32;
    inline constexpr std::size_t salt_max_size = 64;
    
    // Entropy
    inline constexpr std::size_t entropy_min_size = 16;
    inline constexpr std::size_t entropy_recommended_size = 32;
}

// ───────────────────────────────────────────────────────────────────────────
// Buffer Sizes
// ───────────────────────────────────────────────────────────────────────────
namespace buffers {
    inline constexpr std::size_t hwid_size = 64;
    inline constexpr std::size_t hwid_buffer_size = 65;
    inline constexpr std::size_t serial_size = 47;
    inline constexpr std::size_t serial_buffer_size = 48;
    inline constexpr std::size_t edition_size = 31;
    inline constexpr std::size_t edition_buffer_size = 32;
    inline constexpr std::size_t modules_size = 511;
    inline constexpr std::size_t modules_buffer_size = 512;
    inline constexpr std::size_t account_id_size = 47;
    inline constexpr std::size_t account_id_buffer_size = 48;
    inline constexpr std::size_t path_max_size = 260;
    inline constexpr std::size_t error_msg_size = 256;
    inline constexpr std::size_t challenge_size = 16;
    inline constexpr std::size_t response_size = 32;
}

// ───────────────────────────────────────────────────────────────────────────
// Hardware Identifiers
// ───────────────────────────────────────────────────────────────────────────
namespace hardware {
    inline constexpr std::uint16_t dongle_vendor_id = 0x5348;
    inline constexpr std::uint16_t dongle_product_id = 0x4552;
    inline constexpr std::uint32_t dongle_timeout_detect_ms = 2000;
    inline constexpr std::uint32_t dongle_timeout_challenge_ms = 5000;
}

// ───────────────────────────────────────────────────────────────────────────
// Magic Numbers
// ───────────────────────────────────────────────────────────────────────────
namespace magic {
    inline constexpr std::uint32_t license = 0x53544C49;
    inline constexpr std::uint32_t backup = 0x5354424B;
    inline constexpr std::uint32_t vault = 0x5354564C;
    inline constexpr std::uint32_t config = 0x53544346;
    inline constexpr std::uint32_t session = 0x53545353;
}

// ───────────────────────────────────────────────────────────────────────────
// Status Codes
// ───────────────────────────────────────────────────────────────────────────
enum class [[nodiscard]] status : std::uint32_t {
    // Success
    ok = 0x0000'0000,
    
    // Informational
    more_data = 0x0004'0001,
    emergency_mode = 0x0004'0002,
    expiring_soon = 0x0004'0003,
    grace_period = 0x0004'0004,
    offline_mode = 0x0004'0005,
    
    // General errors
    not_initialized = 0x8004'0001,
    already_initialized = 0x8004'0002,
    invalid_parameter = 0x8004'0003,
    buffer_too_small = 0x8004'0004,
    out_of_memory = 0x8004'0005,
    internal_error = 0x8004'0006,
    not_implemented = 0x8004'0007,
    unsupported_version = 0x8004'0008,
    invalid_state = 0x8004'0009,
    timeout = 0x8004'000A,
    cancelled = 0x8004'000B,
    access_denied = 0x8004'000C,
    
    // Dongle errors
    dongle_not_found = 0x8004'0010,
    dongle_communication = 0x8004'0011,
    dongle_invalid_response = 0x8004'0012,
    dongle_firmware_old = 0x8004'0013,
    dongle_tampered = 0x8004'0014,
    dongle_expired = 0x8004'0015,
    
    // License errors
    license_not_found = 0x8004'0020,
    license_invalid = 0x8004'0021,
    license_expired = 0x8004'0022,
    license_hwid_mismatch = 0x8004'0023,
    license_signature_invalid = 0x8004'0024,
    license_decrypt_failed = 0x8004'0025,
    license_version_mismatch = 0x8004'0026,
    license_revoked = 0x8004'0027,
    license_features_mismatch = 0x8004'0028,
    license_users_exceeded = 0x8004'0029,
    license_devices_exceeded = 0x8004'002A,
    
    // Security errors
    debugger_detected = 0x8004'0030,
    vm_detected = 0x8004'0031,
    analysis_tool_detected = 0x8004'0032,
    runtime_violation = 0x8004'0033,
    session_invalid = 0x8004'0034,
    integrity_check_failed = 0x8004'0035,
    hook_detected = 0x8004'0036,
    injection_detected = 0x8004'0037,
    sandbox_detected = 0x8004'0038,
    
    // Cryptography errors
    crypto_init = 0x8004'0040,
    crypto_encrypt = 0x8004'0041,
    crypto_decrypt = 0x8004'0042,
    crypto_hash = 0x8004'0043,
    crypto_sign = 0x8004'0044,
    crypto_verify = 0x8004'0045,
    crypto_random = 0x8004'0046,
    crypto_key_derivation = 0x8004'0047,
    crypto_invalid_key = 0x8004'0048,
    crypto_invalid_iv = 0x8004'0049,
    crypto_auth_tag_failed = 0x8004'004A,
    crypto_weak_key = 0x8004'004B,
    
    // DPAPI errors
    dpapi_protect = 0x8004'0050,
    dpapi_unprotect = 0x8004'0051,
    dpapi_invalid_data = 0x8004'0052,
    
    // File I/O errors
    file_not_found = 0x8004'0060,
    file_read = 0x8004'0061,
    file_write = 0x8004'0062,
    file_invalid_format = 0x8004'0063,
    file_corrupted = 0x8004'0064,
    file_access_denied = 0x8004'0065,
    file_too_large = 0x8004'0066,
    file_locked = 0x8004'0067
};

[[nodiscard]] constexpr bool succeeded(status s) noexcept {
    return s == status::ok;
}

[[nodiscard]] constexpr bool failed(status s) noexcept {
    return (static_cast<std::uint32_t>(s) & 0x8000'0000) != 0;
}

[[nodiscard]] constexpr bool is_error(status s) noexcept {
    return failed(s);
}

[[nodiscard]] constexpr bool is_info(status s) noexcept {
    return (static_cast<std::uint32_t>(s) & 0xFFFF'0000) == 0x0004'0000;
}

// ───────────────────────────────────────────────────────────────────────────
// License Flags
// ───────────────────────────────────────────────────────────────────────────
enum class license_flags : std::uint32_t {
    none = 0x0000'0000,
    trial = 0x0000'0001,
    subscription = 0x0000'0002,
    perpetual = 0x0000'0004,
    emergency = 0x0000'0008,
    offline = 0x0000'0010,
    dongle_bound = 0x0000'0020,
    hwid_bound = 0x0000'0040,
    cloud_verified = 0x0000'0080,
    floating = 0x0000'0100,
    network = 0x0000'0200,
    educational = 0x0000'0400,
    nfr = 0x0000'0800
};

// ───────────────────────────────────────────────────────────────────────────
// Edition
// ───────────────────────────────────────────────────────────────────────────
enum class edition : std::uint32_t {
    unknown = 0,
    starter = 1,
    professional = 2,
    enterprise = 3,
    datacenter = 4,
    developer = 99
};

// ───────────────────────────────────────────────────────────────────────────
// Init Flags
// ───────────────────────────────────────────────────────────────────────────
enum class init_flags : std::uint32_t {
    none = 0x0000'0000,
    no_runtime_guard = 0x0000'0001,
    no_vm_check = 0x0000'0002,
    no_debugger_check = 0x0000'0004,
    allow_emergency = 0x0000'0008,
    strict_mode = 0x0000'0010,
    offline_only = 0x0000'0020
};

// ───────────────────────────────────────────────────────────────────────────
// Thread Safety
// ───────────────────────────────────────────────────────────────────────────
enum class thread_safety : std::uint32_t {
    unsafe = 0,
    safe_read = 1,
    safe_full = 2
};

// ───────────────────────────────────────────────────────────────────────────
// POD Structures (C-compatible for P/Invoke)
// ───────────────────────────────────────────────────────────────────────────
#pragma pack(push, 8)

struct version_info {
    std::uint32_t cb_size{sizeof(version_info)};
    std::uint32_t version{1};
    std::uint32_t api_version{shoutech::security::api_version};
    std::uint32_t major{version_major};
    std::uint32_t minor{version_minor};
    std::uint32_t patch{version_patch};
    std::uint32_t build{version_build};
    char version_string[16]{};
    char build_date[16]{};
    char build_time[16]{};
    char compiler[32]{};
    std::uint32_t feature_flags{};
    std::uint32_t reserved[3]{};
};

struct init_params {
    std::uint32_t cb_size{sizeof(init_params)};
    std::uint32_t version{1};
    const std::uint8_t* license_data{};
    std::uint32_t license_data_len{};
    const wchar_t* config_path{};
    std::uint32_t flags{};
    std::uint32_t timeout_ms{};
    const std::uint8_t* custom_entropy{};
    std::uint32_t custom_entropy_len{};
    std::uint32_t reserved[4]{};
};

struct session_info {
    std::uint32_t cb_size{sizeof(session_info)};
    std::uint32_t version{1};
    bool valid{};
    bool emergency_mode{};
    status last_status{status::not_initialized};
    edition edition_type{edition::unknown};
    std::uint32_t license_flags{};
    std::uint32_t max_devices{};
    std::uint32_t max_companies{};
    std::uint32_t max_users{};
    std::uint32_t days_remaining{};
    std::uint32_t minutes_until_check{};
    std::uint64_t session_id{};
    char hwid[buffers::hwid_buffer_size]{};
    char edition_name[buffers::edition_buffer_size]{};
    char account_id[buffers::account_id_buffer_size]{};
    char expires_at[32]{};
    char modules[buffers::modules_buffer_size]{};
    std::uint32_t reserved[4]{};
};

struct dongle_info {
    std::uint32_t cb_size{sizeof(dongle_info)};
    std::uint32_t version{1};
    bool present{};
    std::uint16_t vendor_id{};
    std::uint16_t product_id{};
    std::uint16_t firmware_version{};
    std::uint16_t hardware_revision{};
    char serial[buffers::serial_buffer_size]{};
    char manufacturer[64]{};
    char product[64]{};
    std::uint32_t capabilities{};
    std::uint32_t reserved[4]{};
};

struct crypto_params {
    std::uint32_t cb_size{sizeof(crypto_params)};
    std::uint32_t version{1};
    const std::uint8_t* key{};
    std::uint32_t key_len{};
    const std::uint8_t* iv{};
    std::uint32_t iv_len{};
    const std::uint8_t* aad{};
    std::uint32_t aad_len{};
    std::uint32_t flags{};
    std::uint32_t reserved[3]{};
};

struct file_header {
    std::uint32_t magic{magic::backup};
    std::uint32_t version{1};
    std::uint32_t flags{};
    std::uint32_t compression_type{};
    std::uint8_t salt[crypto::salt_recommended_size]{};
    std::uint8_t iv[crypto::aes_iv_size]{};
    std::uint8_t tag[crypto::aes_tag_size]{};
    std::uint64_t original_size{};
    std::uint64_t encrypted_size{};
    std::uint64_t timestamp{};
    std::uint32_t crc32{};
    std::uint8_t reserved[12]{};
};

#pragma pack(pop)

// Compile-time validations
static_assert(sizeof(version_info) == 128);
static_assert(sizeof(init_params) == 64);
static_assert(sizeof(session_info) == 848);
static_assert(sizeof(dongle_info) == 176);
static_assert(sizeof(crypto_params) == 48);
static_assert(sizeof(file_header) == 144);

static_assert(sizeof(version_info) % 8 == 0);
static_assert(sizeof(init_params) % 8 == 0);
static_assert(sizeof(session_info) % 8 == 0);
static_assert(sizeof(dongle_info) % 8 == 0);
static_assert(sizeof(crypto_params) % 8 == 0);
static_assert(sizeof(file_header) % 8 == 0);

static_assert(std::is_standard_layout_v<version_info>);
static_assert(std::is_standard_layout_v<init_params>);
static_assert(std::is_standard_layout_v<session_info>);
static_assert(std::is_standard_layout_v<dongle_info>);
static_assert(std::is_standard_layout_v<crypto_params>);
static_assert(std::is_standard_layout_v<file_header>);

// ───────────────────────────────────────────────────────────────────────────
// C++ Modern Helpers
// ───────────────────────────────────────────────────────────────────────────

// Modern alternative to ST_INIT_STRUCT macro
// ───────────────────────────────────────────────────────────────────────────
// C++ Modern Helpers
// ───────────────────────────────────────────────────────────────────────────

// Modern alternative to ST_INIT_STRUCT macro
template<typename T>
void init_struct(T& s) noexcept  // ✅ أزل constexpr
    requires std::is_standard_layout_v<T>
{
    std::memset(&s, 0, sizeof(T));
    if constexpr (requires { s.cb_size; }) {
        s.cb_size = sizeof(T);
    }
    if constexpr (requires { s.version; }) {
        s.version = 1;
    }
}

// Secure zero memory (modern)
inline void secure_zero(std::span<std::uint8_t> buffer) noexcept {
    volatile std::uint8_t* p = buffer.data();
    for (std::size_t i = 0; i < buffer.size(); ++i) {
        p[i] = 0;
    }
    std::atomic_thread_fence(std::memory_order_seq_cst); // ✅ thread_fence بدل signal_fence
}

// Constant-time compare (modern)
[[nodiscard]] inline bool constant_time_compare(
    std::span<const std::uint8_t> a,
    std::span<const std::uint8_t> b
) noexcept {
    if (a.size() != b.size()) return false;
    
    std::uint8_t result = 0;
    for (std::size_t i = 0; i < a.size(); ++i) {
        result |= a[i] ^ b[i];
    }
    
    // Ensure result is not optimized away
    std::atomic_thread_fence(std::memory_order_seq_cst);
    *(volatile std::uint8_t*)&result = result; // Force observable side effect
    
    return result == 0;
}

} // namespace shoutech::security

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 5: C API (for P/Invoke compatibility)
// ═══════════════════════════════════════════════════════════════════════════
// All functions use __stdcall convention and C linkage for C# interop
// Thread safety is documented per function
// ═══════════════════════════════════════════════════════════════════════════

// Type aliases for C API
using ST_STATUS = shoutech::security::status;
using ST_EDITION = shoutech::security::edition;
using ST_VERSION_INFO = shoutech::security::version_info;
using ST_INIT_PARAMS = shoutech::security::init_params;
using ST_SESSION_INFO = shoutech::security::session_info;
using ST_DONGLE_INFO = shoutech::security::dongle_info;
using ST_CRYPTO_PARAMS = shoutech::security::crypto_params;
using ST_FILE_HEADER = shoutech::security::file_header;

// ───────────────────────────────────────────────────────────────────────────
// Initialization & Lifecycle
// Thread safety: Must be called from single thread
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_Initialize(
    ST_IN const ST_INIT_PARAMS* params
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_InitializeSimple(
    ST_BUFFER(license_data_len) const std::uint8_t* license_data,
    std::uint32_t license_data_len
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_Shutdown(void);

ST_NODISCARD ST_API bool ST_CALL ST_IsInitialized(void);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_GetVersion(
    ST_OUT ST_VERSION_INFO* version_info
);

ST_NODISCARD ST_API std::uint32_t ST_CALL ST_GetAPIVersion(void);

// ───────────────────────────────────────────────────────────────────────────
// Session Validation
// Thread safety: All functions thread-safe
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_ValidateSession(
    ST_OUT ST_SESSION_INFO* session_info
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_GetLastStatus(void);

ST_NODISCARD ST_API bool ST_CALL ST_IsLicenseValid(void);

ST_NODISCARD ST_API bool ST_CALL ST_IsEmergencyMode(void);

ST_NODISCARD ST_API ST_EDITION ST_CALL ST_GetEdition(void);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_RefreshSession(void);

// ───────────────────────────────────────────────────────────────────────────
// Hardware Identification
// Thread safety: Thread-safe
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_GetHardwareId(
    ST_OUT_BUFFER(buffer_size) char* buffer,
    std::uint32_t buffer_size,
    ST_OUT_OPT std::uint32_t* required_size
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_GetHardwareIdW(
    ST_OUT_BUFFER(buffer_size) wchar_t* buffer,
    std::uint32_t buffer_size,
    ST_OUT_OPT std::uint32_t* required_size
);

// ───────────────────────────────────────────────────────────────────────────
// Dongle
// Thread safety: Thread-safe with internal locking
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_DetectDongle(
    ST_OUT ST_DONGLE_INFO* dongle_info
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_ChallengeDongle(
    ST_BUFFER(challenge_len) const std::uint8_t* challenge,
    std::uint32_t challenge_len,
    ST_OUT_BUFFER(response_buffer_size) std::uint8_t* response,
    std::uint32_t response_buffer_size,
    ST_OUT std::uint32_t* response_len
);

ST_NODISCARD ST_API bool ST_CALL ST_IsDonglePresent(void);

// ───────────────────────────────────────────────────────────────────────────
// Cryptography
// Thread safety: All functions thread-safe
// Implementation: BCrypt (Windows CNG)
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_RandomBytes(
    ST_OUT_BUFFER(buffer_size) std::uint8_t* buffer,
    std::uint32_t buffer_size
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_HashSHA256(
    ST_BUFFER(data_len) const std::uint8_t* data,
    std::uint32_t data_len,
    ST_OUT_BUFFER(32) std::uint8_t* hash
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_HashSHA512(
    ST_BUFFER(data_len) const std::uint8_t* data,
    std::uint32_t data_len,
    ST_OUT_BUFFER(64) std::uint8_t* hash
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_HashFileSHA256(
    ST_IN const wchar_t* file_path,
    ST_OUT_BUFFER(32) std::uint8_t* hash
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_HMAC_SHA256(
    ST_BUFFER(key_len) const std::uint8_t* key,
    std::uint32_t key_len,
    ST_BUFFER(data_len) const std::uint8_t* data,
    std::uint32_t data_len,
    ST_OUT_BUFFER(32) std::uint8_t* mac
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_DeriveKeyPBKDF2(
    ST_BUFFER(password_len) const std::uint8_t* password,
    std::uint32_t password_len,
    ST_BUFFER(salt_len) const std::uint8_t* salt,
    std::uint32_t salt_len,
    std::uint32_t iterations,
    ST_OUT_BUFFER(key_len) std::uint8_t* key,
    std::uint32_t key_len
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_EncryptAES256GCM(
    ST_IN const ST_CRYPTO_PARAMS* params,
    ST_BUFFER(plaintext_len) const std::uint8_t* plaintext,
    std::uint32_t plaintext_len,
    ST_OUT_BUFFER(ciphertext_buffer_size) std::uint8_t* ciphertext,
    std::uint32_t ciphertext_buffer_size,
    ST_OUT std::uint32_t* ciphertext_len,
    ST_OUT_BUFFER(16) std::uint8_t* tag
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_DecryptAES256GCM(
    ST_IN const ST_CRYPTO_PARAMS* params,
    ST_BUFFER(ciphertext_len) const std::uint8_t* ciphertext,
    std::uint32_t ciphertext_len,
    ST_BUFFER(16) const std::uint8_t* tag,
    ST_OUT_BUFFER(plaintext_buffer_size) std::uint8_t* plaintext,
    std::uint32_t plaintext_buffer_size,
    ST_OUT std::uint32_t* plaintext_len
);

// ───────────────────────────────────────────────────────────────────────────
// Secure Storage (DPAPI)
// Thread safety: Thread-safe
// Platform: Windows only
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_ProtectData(
    ST_BUFFER(plaintext_len) const std::uint8_t* plaintext,
    std::uint32_t plaintext_len,
    ST_BUFFER(entropy_len) const std::uint8_t* entropy,
    std::uint32_t entropy_len,
    bool machine_scope,
    ST_OUT_BUFFER(protected_buffer_size) std::uint8_t* protected_data,
    std::uint32_t protected_buffer_size,
    ST_OUT std::uint32_t* protected_len
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_UnprotectData(
    ST_BUFFER(protected_len) const std::uint8_t* protected_data,
    std::uint32_t protected_len,
    ST_BUFFER(entropy_len) const std::uint8_t* entropy,
    std::uint32_t entropy_len,
    ST_OUT_BUFFER(plaintext_buffer_size) std::uint8_t* plaintext,
    std::uint32_t plaintext_buffer_size,
    ST_OUT std::uint32_t* plaintext_len
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_GetVaultKey(
    ST_OUT_BUFFER(key_buffer_size) std::uint8_t* key,
    std::uint32_t key_buffer_size,
    ST_OUT std::uint32_t* key_len
);

// ───────────────────────────────────────────────────────────────────────────
// File Encryption
// Thread safety: Thread-safe
// Performance: Memory usage = 2x file size for large files
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_EncryptFile(
    ST_IN const wchar_t* input_path,
    ST_IN const wchar_t* output_path,
    ST_BUFFER(key_len) const std::uint8_t* key,
    std::uint32_t key_len
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_DecryptFile(
    ST_IN const wchar_t* input_path,
    ST_IN const wchar_t* output_path,
    ST_BUFFER(key_len) const std::uint8_t* key,
    std::uint32_t key_len
);

// ───────────────────────────────────────────────────────────────────────────
// Utility
// Thread safety: All functions thread-safe
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_GetStatusMessage(
    ST_STATUS status,
    ST_OUT_BUFFER(buffer_size) wchar_t* buffer,
    std::uint32_t buffer_size,
    ST_OUT_OPT std::uint32_t* required_size
);

// Secure zero memory (C API) — implemented in SecEng.dll (prot.cpp / modules)
ST_API void ST_CALL ST_SecureZeroMemory(
    void* buffer,
    std::size_t size
);

// ═══════════════════════════════════════════════════════════════════════════
// SECTION 6: COMPILER SECURITY DIRECTIVES
// ═══════════════════════════════════════════════════════════════════════════

#if defined(_MSC_VER)
    #pragma strict_gs_check(on)
    #pragma comment(linker, "/GUARD:CF")
    #pragma comment(linker, "/DYNAMICBASE")
    #pragma comment(linker, "/HIGHENTROPYVA")
    #pragma comment(linker, "/NXCOMPAT")
    #if _MSC_VER >= 1929
        #pragma comment(linker, "/CETCOMPAT")
    #endif
    #pragma comment(linker, "/OPT:REF")
    #pragma comment(linker, "/OPT:ICF")
#endif


// ───────────────────────────────────────────────────────────────────────────
// STBK2 Company Backup Envelope (native)
// Layout: [256-byte header][metadata][ciphertext]
// ───────────────────────────────────────────────────────────────────────────

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_CreateStbk2Backup(
    ST_BUFFER(32) const std::uint8_t* kek32,
    ST_BUFFER(16) const std::uint8_t* company_id16,
    ST_OPT const char* provider,
    ST_OPT const char* key_id,
    ST_BUFFER(metadata_len) const std::uint8_t* metadata,
    std::uint32_t metadata_len,
    ST_BUFFER(plaintext_len) const std::uint8_t* plaintext,
    std::uint32_t plaintext_len,
    ST_OUT_BUFFER(out_buffer_size) std::uint8_t* out_buffer,
    std::uint32_t out_buffer_size,
    ST_OUT std::uint32_t* out_len
);

ST_NODISCARD ST_API ST_STATUS ST_CALL ST_ValidateStbk2Header(
    ST_BUFFER(file_len) const std::uint8_t* file_data,
    std::uint32_t file_len
);

// ═══════════════════════════════════════════════════════════════════════════
// END OF FILE
// ═══════════════════════════════════════════════════════════════════════════

#endif // SHOUTECH_SEC_H_9DA8F3E1_4B72_4C9A_B8E5_2F7A3D1C0E96_