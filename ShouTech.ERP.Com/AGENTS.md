# Upgrade Engineering Rules

These rules are mandatory for all work in this repository and take precedence over older plans where they conflict with the current product direction.

## Product standard

Build ShouTech as a high-grade, institution-ready commercial ERP. Treat accounting integrity, data safety, security, auditability, multi-company isolation, performance, localization, maintainability, and operational reliability as release requirements—not optional polish. A screen is not a completed feature: every workflow must be connected to authorization, application services, persistence, accounting/inventory effects where applicable, audit history, reporting, and automated tests.

## Repository and codebase discipline

1. **Consolidate coherent files.** Reduce unnecessary file count by merging files that form one cohesive responsibility or implementation unit. Keep clear boundaries between domain, application, infrastructure, UI, tests, and database assets. Do not create giant files or merge unrelated responsibilities merely to reduce the count.
2. **Remove obsolete or duplicate files only with evidence.** Before deleting anything, identify all references, build inclusion, runtime loading, migration history, reflection/resource use, and whether a superior replacement is complete. Preserve compatibility where required. Never delete an applied database migration or historical data artifact just because it looks duplicated.
3. **Keep names concise and consistent.** Prefer short, meaningful file names (normally 3–4 characters or compact words/abbreviations where they remain understandable). Avoid long descriptive filenames, repeated prefixes, and redundant folder nesting. Do not rename public or migration identifiers without a compatibility plan.
4. **One application startup path.** The product must have one authoritative launch/bootstrap entry point, located as close to the repository/application root as the existing architecture safely allows. All desktop startup, dependency registration, configuration, database initialization, logging, and shutdown must flow through that entry point. Remove competing launch paths only after proving they are unused and preserving supported deployment scenarios.

## Research and implementation standard

5. **Research before adopting code.** For each substantial feature or technical area, search GitHub and the wider internet for mature, relevant open-source implementations and established patterns. Compare at least the strongest relevant candidates against ShouTech's requirements, license, maintenance status, security, database/provider compatibility, architecture, and test coverage. Record sources and the reason for selecting, adapting, or rejecting each candidate.
6. **Adapt; do not transplant blindly.** Any adopted code or design must be adapted to ShouTech's domain model, existing architecture, SQLite and PostgreSQL support, localization/resource system, permissions, audit model, accounting rules, and UI conventions. Respect licenses and preserve attribution/NOTICE obligations. Do not copy code with incompatible or unclear licensing.
7. **Prefer real integration over placeholders.** Do not mark functionality complete when it only has a window, mock response, local-only state, or disconnected service. Avoid duplicate implementations; extend the canonical implementation unless a deliberate migration plan says otherwise.

## Delivery and verification

8. **Test by completed phase, not by every tiny edit.** Group related changes into a coherent phase, then run the appropriate build, automated tests, database migration checks for both SQLite and PostgreSQL, and relevant integration/regression tests at the phase boundary. Do not interrupt implementation to run the full test suite after each small operation. Run targeted checks during a phase only when needed to diagnose a concrete risk or failure.
9. **Do not defer all verification.** Each phase must end with recorded evidence: exact commands/workflows, commit tested, pass/fail results, unresolved risks, and next phase. Never report a check as successful unless it actually ran successfully.
10. **Protect data and migration history.** Database schema changes belong in the database migration system, with provider parity. Never renumber or rewrite migrations that may have been applied. Use new forward-only migrations for corrections, and test fresh-install plus upgrade-from-existing-database paths.
11. **Keep source language-neutral.** Source code, identifiers, and comments must be English. User-facing strings must come from localization/resource files; do not hardcode Arabic UI text in source files.
12. **Work incrementally on the designated upgrade branch.** Keep the base branch intact, make coherent commits, document architectural decisions, and do not claim completion until end-to-end behavior and phase tests prove it.

## Required phase checklist

Before closing a phase, verify:
- scope and acceptance criteria are explicit;
- existing implementations and call sites were inspected before replacement;
- relevant GitHub/web research and licensing were reviewed;
- database changes are safe for SQLite and PostgreSQL;
- permissions, validation, audit, error handling, and localization are covered;
- UI, services, persistence, and downstream effects are connected;
- tests cover success, failure, edge, and regression cases;
- the application still has one supported startup path;
- obsolete files were removed only when proven safe;
- phase evidence and remaining gaps are documented.
