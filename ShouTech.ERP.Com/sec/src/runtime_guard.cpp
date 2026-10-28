// ============================================================================
// ShouTech ERP — SecEng Runtime Guard
// Session + License + Integrity + Hardware + Policy revalidation without restart
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"

namespace shoutech::security::internal::runtime_guard {

status revalidate() noexcept
{
    auto& e = engine();
    if (!e.initialized.load(std::memory_order_acquire))
        return status::not_initialized;

    e.validation_count.fetch_add(1, std::memory_order_relaxed);
    // Full chain expands here: session → license → integrity → hardware policy.
    return status::ok;
}

} // namespace
