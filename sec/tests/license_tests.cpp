#include "sec.h"

#include <cstdint>
#include <cstdio>

using shoutech::security::status;

int main()
{
    const std::uint8_t forgedPayload[] = {
        0x53, 0x54, 0x4C, 0x43, 0x01, 0x00, 0x00, 0x00,
        0x50, 0x52, 0x4F, 0x46, 0x45, 0x53, 0x53, 0x49
    };

    const auto result = ST_InitializeSimple(
        forgedPayload, static_cast<std::uint32_t>(sizeof(forgedPayload)));

    if (result == status::ok || ST_IsInitialized() || ST_IsLicenseValid()) {
        std::fprintf(stderr, "Unverified license payload was accepted.\n");
        return 1;
    }

    if (result == status::license_invalid) {
        std::puts("PASS: unverified license payload rejected");
        return 0;
    }

    std::fprintf(stderr, "FAIL: expected license_invalid, received 0x%08X\n",
        static_cast<unsigned int>(result));
    return 2;
}
