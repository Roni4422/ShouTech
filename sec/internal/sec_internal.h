// ============================================================================
// ShouTech ERP — SecEng Internal: Engine State & Diagnostics
// ============================================================================
#pragma once

#include <Windows.h>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

#include "sec.h"

namespace shoutech::security::internal {

struct engine_state
{
    std::atomic<bool> initialized{ false };
    std::atomic<status> last_status{ status::not_initialized };
    std::atomic<std::uint32_t> validation_count{ 0 };
    std::atomic<bool> emergency_mode{ false };
    edition current_edition{ edition::starter };
    std::mutex mutex;
    std::wstring last_diagnostic;
    char last_diagnostic_id[64]{};
};

[[nodiscard]] engine_state& engine() noexcept;

void set_status(status s) noexcept;
void set_diagnostic(status s, const char* diagnostic_id, const wchar_t* safe_message) noexcept;

[[nodiscard]] const char* status_category(status s) noexcept;
[[nodiscard]] const wchar_t* status_safe_message(status s) noexcept;

// STBK2 format constants (backup security envelope)
namespace stbk2 {
    inline constexpr std::uint32_t magic = 0x324B4254; // 'TBK2' little-endian visual
    inline constexpr std::uint16_t format_version = 2;
    inline constexpr std::uint16_t header_size = 256;

    enum class encryption_algorithm : std::uint16_t
    {
        none = 0,
        aes256_gcm = 1
    };

    #pragma pack(push, 1)
    struct header
    {
        std::uint32_t magic{ magic };
        std::uint16_t format_version{ stbk2::format_version };
        std::uint16_t header_size{ stbk2::header_size };
        std::uint8_t  backup_id[16]{};
        std::uint8_t  company_id[16]{};
        char          provider[32]{};
        std::uint64_t created_at_unix{};
        encryption_algorithm algorithm{ encryption_algorithm::aes256_gcm };
        std::uint16_t reserved0{};
        char          key_id[48]{};
        std::uint8_t  wrapped_dek[48]{};   // room for wrapped 32-byte DEK + overhead
        std::uint32_t wrapped_dek_len{};
        std::uint8_t  payload_nonce[12]{};
        std::uint8_t  payload_tag[16]{};
        std::uint32_t metadata_len{};
        std::uint64_t payload_len{};
        std::uint8_t  reserved[48]{};
    };
    #pragma pack(pop)

    static_assert(sizeof(header) <= 256, "STBK2 header must fit reserved layout");
}

// Key vault record (in-memory / future persistence)
enum class key_purpose : std::uint16_t
{
    application = 1,
    license = 2,
    backup = 3,
    cloud = 4,
    database = 5,
    recovery = 6
};

enum class key_status : std::uint16_t
{
    active = 1,
    retired = 2,
    revoked = 3
};

struct key_record
{
    char key_id[48]{};
    key_purpose purpose{ key_purpose::application };
    std::uint64_t created_at_unix{};
    char algorithm[32]{ "AES-256-GCM" };
    std::uint16_t version{ 1 };
    char scope[32]{ "local" };
    key_status status{ key_status::active };
    std::uint8_t key_material[32]{};
    std::uint32_t key_len{ 32 };
};

} // namespace shoutech::security::internal
