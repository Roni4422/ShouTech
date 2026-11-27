// ============================================================================
// ShouTech SecEng — Crypto smoke tests (compile with SecEng test target)
// ============================================================================
#include "sec.h"
#include "../internal/crypto_internal.h"

#include <cstdio>
#include <cstring>
#include <vector>

using shoutech::security::status;
namespace ic = shoutech::security::internal::crypto;

static int failures = 0;

static void expect(bool cond, const char* name)
{
    if (!cond)
    {
        std::printf("FAIL: %s\n", name);
        ++failures;
    }
    else
    {
        std::printf("PASS: %s\n", name);
    }
}

int main()
{
    std::uint8_t key[32]{};
    std::uint8_t nonce[12]{};
    expect(ic::random_bytes(key) == status::ok, "random key");
    expect(ic::random_bytes(nonce) == status::ok, "random nonce");
    expect(ic::is_valid_aes256_key(key), "valid key");
    expect(!ic::is_valid_aes256_key(std::span<const std::uint8_t>(std::uint8_t{0}, 0)), "reject empty key");

    const char* msg = "ShouTech ERP enterprise backup payload";
    std::span<const std::uint8_t> plain(
        reinterpret_cast<const std::uint8_t*>(msg),
        std::strlen(msg));

    std::vector<std::uint8_t> cipher(plain.size());
    std::uint8_t tag[16]{};
    std::uint32_t ctLen = 0;
    auto st = ic::aes256_gcm_encrypt(key, nonce, {}, plain, cipher, ctLen, tag);
    expect(st == status::ok, "gcm encrypt");

    std::vector<std::uint8_t> recovered(plain.size());
    std::uint32_t ptLen = 0;
    st = ic::aes256_gcm_decrypt(key, nonce, {}, cipher, tag, recovered, ptLen);
    expect(st == status::ok, "gcm decrypt");
    expect(ptLen == plain.size() && std::memcmp(recovered.data(), plain.data(), plain.size()) == 0, "roundtrip");

    tag[0] ^= 0xFF;
    st = ic::aes256_gcm_decrypt(key, nonce, {}, cipher, tag, recovered, ptLen);
    expect(st == status::crypto_auth_tag_failed, "auth tag fails");

    std::printf("Failures: %d\n", failures);
    return failures == 0 ? 0 : 1;
}
