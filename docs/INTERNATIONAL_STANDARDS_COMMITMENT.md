# ShouTech ERP — Commitment to International Standards

**Status:** Binding product and engineering policy  
**Effective:** 2026-11-27  
**Language of source of truth:** English (this document). Arabic translation of user-facing summary may be maintained separately.

## 1. Principle

ShouTech ERP is engineered as a commercial, offline-first enterprise system. It must remain aligned with internationally recognized practices for:

- accounting integrity and auditability  
- information security  
- software quality and evidence-based delivery  
- localization and accessibility  
- tax and regulatory opt-in (never false country compliance claims)

**Formal certification** (e.g. full IFRS financial-statement suite, ISO 27001 certificate, WCAG formal audit, ZATCA production approval) is **not** claimed until independent evidence exists. Alignment of design, code, tests, and documentation with the substance of these standards **is** mandatory and is part of Gate 0 and every subsequent phase.

## 2. Accounting integrity (primary)

| Requirement | International basis | ShouTech rule |
|-------------|---------------------|---------------|
| Double-entry bookkeeping | Universal accounting principle; IFRS Conceptual Framework | Every posted journal must balance (Σ debit = Σ credit) in company base currency and document currency where applicable. Unbalanced postings are rejected. |
| Accrual and period discipline | IFRS Conceptual Framework | Posting only into open fiscal periods; closed periods/years refuse new postings. |
| Faithful representation & audit trail | IFRS Conceptual Framework; audit best practice | Posted documents are immutable. Correction is only by controlled reversal/cancellation documents that preserve original history, actor, timestamp, and reason. |
| No silent deletion of financial evidence | Audit and legal expectation worldwide | Permanent hard-delete of posted financial history is forbidden. Recycle-bin / soft-delete never erases posted ledger effects. |
| Company isolation | Multi-entity control practice | All company-owned records and queries are scoped to the active company; cross-company access is denied by default. |
| Tax is opt-in | Good practice for multi-jurisdiction products | Selecting a country does **not** activate tax. Explicit administrator enablement is required. No automatic creation of tax codes/rates/postings. |

Tests for balance, period lock, reversal-once, and company scope are mandatory acceptance criteria for accounting features.

## 3. Security controls

| Area | Reference | ShouTech rule |
|------|-----------|---------------|
| Password storage | OWASP Password Storage Cheat Sheet | PBKDF2-HMAC-SHA512 (or stronger successor), high iteration count, unique salt, constant-time compare. Rehash on login when work factor rises. |
| Password length | OWASP Authentication Cheat Sheet | Minimum **8** characters for account passwords (composition free). |
| Authentication abuse | OWASP ASVS / common practice | Login lockout with configurable thresholds; security audit events for success/failure/lock. |
| Injection | OWASP Top 10 | Parameterized SQL only for values. Identifier allow-lists for dynamic table/column names. No string-concatenated user input into SQL. |
| Secrets | Industry practice / ISO 27001 Annex A themes | No production secrets in source control. Connection strings and signing keys from environment or platform-protected storage. |
| Authorization | Least privilege | Permissions enforced in application/service layer, not only by hiding UI. RBAC + optional per-user overrides. |
| Audit of security events | ISO 27001-aligned logging themes | Security-relevant events written to durable audit store without passwords or secrets. |

## 4. Software quality and delivery

- Features are **Done** only when UI path, service logic, persistence, permissions, audit, localization, and automated tests pass for the stated scope.  
- Database schema ownership remains in `dba/mig/{provider}`; migrations are forward-only, hashed, and idempotent on re-apply.  
- Provider parity (SQLite and PostgreSQL) is required for business semantics.  
- CI evidence (migration smoke on Ubuntu, full build/tests on Windows) is the release gate, not local claims.

## 5. Localization, RTL, and accessibility

- English is the source language of code, identifiers, and comments.  
- User-visible strings come from localization resources (Ar, En, Fr, Tr at minimum).  
- RTL and LTR layouts must both be usable for primary workflows.  
- Progressive alignment with **WCAG 2.1 Level AA** for keyboard navigation, focus order, contrast, and labels on major windows (tracked as a quality bar, not an immediate formal certificate).

## 6. Tax, e-invoicing, and country packs

- Country tax/e-invoicing packs are based on **official authority specifications** (e.g. ZATCA FATOORA, ETA, FTA, HMRC MTD, etc.).  
- A pack is not “complete” or marketable until technical conformance evidence and a named legal/domain review exist.  
- Starter rates or country codes alone never constitute compliance.

## 7. Privacy and data protection (baseline)

Where personal data is stored (users, parties, employees):

- Collect and retain only what the product needs.  
- Access is permission-controlled and company-scoped.  
- Exports and backups that contain personal or financial data are treated as sensitive.  
- Full GDPR/CCPA formal compliance programs are product decisions for specific markets and require separate legal review.

## 8. What this means for engineering work

1. Gate 0 and Phase 1 prioritise accounting balance, reversal integrity, period locks, company isolation, and permission enforcement — these are standards-aligned **and** release blockers.  
2. New financial features must include tests for the invariants in §2.  
3. Security changes follow the controls in §3; regressions that weaken hashing, parameterization, or audit are P0.  
4. Marketing and documentation must not claim certified compliance that the evidence does not support.  
5. This document is updated when a standard is formally targeted or when evidence upgrades a capability from “aligned in design” to “verified for stated scope”.

## 9. Explicit non-claims (honest boundary)

Until independent evidence exists, ShouTech does **not** claim:

- Full IFRS financial-statement production and disclosure compliance  
- ISO 27001 or SOC 2 certification  
- Formal WCAG AA audit certificate  
- Production ZATCA / other authority e-invoicing certification  
- Legal advice or regulatory approval for any jurisdiction  

Alignment of core accounting and security behaviour with the substance of the standards above **is** required and is already partially implemented (double-entry posting, reversals, period locks, RBAC, parameterized access, security audit log, tax opt-in). Remaining gaps are tracked in the Developer Roadmap and Competitive Gap Assessment and are closed with tests and CI evidence.

---

*Approved as binding policy for the reduce-2-upgrade / stabilization line of work. All subsequent phases inherit these constraints.*
