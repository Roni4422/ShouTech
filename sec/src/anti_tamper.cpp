// ============================================================================
// ShouTech ERP — SecEng Anti-Tamper (policy-driven, low false-positives)
// Detection → Risk Assessment → Policy → Action
// NEVER: "debugger process exists" == immediate shutdown for ERP desktops.
// ============================================================================
#define ST_SEC_BUILDING_DLL
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include "sec.h"
#include "../internal/sec_internal.h"

namespace shoutech::security::internal::anti_tamper {

enum class risk_level : std::uint8_t
{
    none = 0,
    low = 1,
    medium = 2,
    high = 3,
    critical = 4
};

enum class policy_action : std::uint8_t
{
    log = 0,
    warn = 1,
    restrict = 2,
    require_revalidation = 3,
    block = 4
};

policy_action action_for(risk_level risk) noexcept
{
    switch (risk)
    {
    case risk_level::low: return policy_action::log;
    case risk_level::medium: return policy_action::warn;
    case risk_level::high: return policy_action::restrict;
    case risk_level::critical: return policy_action::block;
    default: return policy_action::log;
    }
}

// Presence of developer tools (VS, Process Explorer, Wireshark) is NOT critical
// by itself on an ERP workstation. Only active attach / injection raises risk.

} // namespace
