// ============================================================================
// ShouTech ERP — SecEng Internal: Secure Memory
// Enterprise Security Engine | Windows x64 | C++20
// ============================================================================
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <atomic>
#include <cstring>

namespace shoutech::security::internal {

/// Securely zero a buffer. Resistant to dead-store elimination on MSVC/Clang/GCC.
inline void secure_zero(void* ptr, std::size_t size) noexcept
{
    if (ptr == nullptr || size == 0)
        return;

    ::SecureZeroMemory(ptr, size);
}

inline void secure_zero(std::span<std::uint8_t> buffer) noexcept
{
    secure_zero(buffer.data(), buffer.size());
}

/// Constant-time comparison. Returns true only if lengths match and content matches.
[[nodiscard]] inline bool constant_time_equal(
    const std::uint8_t* a,
    const std::uint8_t* b,
    std::size_t len) noexcept
{
    if (a == nullptr || b == nullptr)
        return len == 0;

    std::uint8_t diff = 0;
    for (std::size_t i = 0; i < len; ++i)
        diff = static_cast<std::uint8_t>(diff | (a[i] ^ b[i]));

    std::atomic_thread_fence(std::memory_order_seq_cst);
    return diff == 0;
}

[[nodiscard]] inline bool constant_time_equal(
    std::span<const std::uint8_t> a,
    std::span<const std::uint8_t> b) noexcept
{
    if (a.size() != b.size())
        return false;
    return constant_time_equal(a.data(), b.data(), a.size());
}

/// RAII buffer that zeros on destruction.
template <std::size_t N>
class secure_buffer
{
public:
    secure_buffer() noexcept { secure_zero(data_, N); }
    ~secure_buffer() noexcept { secure_zero(data_, N); }

    secure_buffer(const secure_buffer&) = delete;
    secure_buffer& operator=(const secure_buffer&) = delete;

    [[nodiscard]] std::uint8_t* data() noexcept { return data_; }
    [[nodiscard]] const std::uint8_t* data() const noexcept { return data_; }
    [[nodiscard]] constexpr std::size_t size() const noexcept { return N; }
    [[nodiscard]] std::span<std::uint8_t> span() noexcept { return { data_, N }; }

private:
    alignas(16) std::uint8_t data_[N]{};
};

} // namespace shoutech::security::internal
