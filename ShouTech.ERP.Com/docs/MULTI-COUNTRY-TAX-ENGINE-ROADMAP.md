# Multi-Country Tax Engine — Product and Engineering Specification

## Product commitment

ShouTech ERP must support configurable tax compliance for companies operating in multiple jurisdictions. Tax support is a first-class accounting capability, not a single percentage field on an invoice. The engine must be extensible through versioned jurisdiction packs and configuration data; adding a country must not require changing invoice arithmetic or GL posting code.

Initial coverage targets:

- Gulf Cooperation Council: Saudi Arabia, Kuwait, United Arab Emirates, Qatar, Bahrain, Oman.
- Levant and Iraq: Jordan, Syria, Iraq, Lebanon, and Palestine where applicable.
- North Africa: Egypt first, with a country-pack structure for additional markets.
- Europe: EU VAT framework plus country-specific rates, registrations, invoice rules, and reporting; support non-EU European jurisdictions through separate packs.
- United States: federal, state, county, city, district and other applicable sales/use tax layers, sourcing rules, exemptions, nexus and filing periods.
- Other countries: generic jurisdiction-pack contract and reusable tax primitives.

Country coverage is a delivery target, not a claim that every jurisdiction is legally certified at the same time. Each production pack must be verified against current official legislation, tax authority specifications, effective dates and local professional review before it is marked compliant.

## Current implementation assessment

The existing `TaxSvc` and `tax_codes` path provide only a basic active-rate lookup and a hard-coded country-default dictionary. Invoice lines carry a percentage and the posting engine can split a tax amount to an input/output VAT account. This is a useful foundation but is not a multi-country compliance engine.

Known architectural risks to resolve before production use:

- Static country percentages are not an authoritative or effective-dated tax source.
- A generic fallback rate must never silently become the legal rate for a company.
- A single rate cannot represent multiple taxes, compound taxes, jurisdiction layers, exemptions, reverse charge, withholding, special schemes or line-level tax treatments.
- Tax calculation, tax determination, GL mapping, statutory reporting and e-invoicing need separate responsibilities.
- Tax evidence and the exact rules used at posting time must be preserved so later rate changes cannot rewrite historical documents.

## Required tax domain model

Implement a jurisdiction-neutral model with at least these concepts:

1. **Tax jurisdiction** — country, subdivision, locality, tax authority, jurisdiction code, effective period and parent jurisdiction.
2. **Tax registration** — company, jurisdiction, registration/VAT/TIN number, registration status, effective dates, filing frequency, tax scheme and evidence.
3. **Tax category and treatment** — standard, reduced, super-reduced, zero-rated, exempt, out-of-scope, non-taxable, reverse-charge, export, import, domestic acquisition, withholding and special schemes as applicable.
4. **Tax code** — stable code, localized description, jurisdiction, tax type, calculation method, recoverability, posting profile and effective-dated rate components.
5. **Tax rule** — product/service category, customer/supplier status, ship-from/ship-to/place-of-supply, transaction type, exemption evidence, registration and rule priority.
6. **Tax component** — percentage, fixed amount, quantity-based amount, tiered amount, compound/stacking behavior, inclusive/exclusive behavior, rounding precision and calculation order.
7. **Tax determination result** — selected rule, jurisdiction, tax code, component breakdown, taxable base, amount, currency, rounding, exemption/reverse-charge reason and rule-version IDs.
8. **Tax return and settlement** — period, boxes/lines, source transactions, adjustments, payments, credits, filing state and immutable submission evidence.
9. **E-invoice / authority submission** — provider/authority, schema version, document UUID, submission state, signed payload reference, response, QR/hash where required, retries and audit trail.

All effective periods must be date-based and non-overlapping where the jurisdiction rules require it. Store rates and calculation parameters as decimal values with explicit precision; never use floating-point arithmetic for money or tax.

## Calculation and posting requirements

- Determine tax from company registration, transaction date, supply type, item/service tax category, customer/supplier status, ship-from/ship-to and jurisdiction rules.
- Support multiple tax components on one line and on one document, including compound taxes and US layered taxes.
- Support tax-inclusive and tax-exclusive prices, discounts before/after tax as required by local law, freight/fees, deposits, credit notes, returns and cancellations.
- Support line-level and document-level rounding policies; preserve calculated component values and reconciliation deltas.
- Support recoverable and non-recoverable input tax, output tax, import tax, reverse-charge self-assessment, withholding tax and tax suspense/clearing accounts.
- Map each tax component to configurable GL accounts by company, jurisdiction, tax type, tax code and transaction direction.
- Preserve the exact tax determination snapshot on the posted document. Reopening or changing a tax master must not alter historical postings.
- Make posting idempotent and transactional. A retry must not duplicate tax ledger, GL, inventory or authority-submission records.
- Returns and reversals must mirror the original tax treatment and original tax-account mapping, with links to the source document.
- Tax must reconcile between document lines, tax summary, tax ledger, GL, return boxes and settlement.

## Country-pack scope

### Saudi Arabia
- VAT registration and tax groups, standard/zero-rated/exempt/out-of-scope treatments, domestic/export/import scenarios and reverse charge where applicable.
- Tax invoice and simplified tax invoice profiles, required seller/buyer data, sequential numbering constraints where applicable, credit/debit notes and QR requirements.
- ZATCA e-invoicing integration architecture with separately versioned clearance/reporting profiles, cryptographic/security requirements, device/onboarding credentials, signed payloads, QR/hash, UUIDs, offline/retry queue and complete authority responses.
- Support ZATCA schema/profile changes through versioned adapters; never hard-code a single schema into invoice posting.

### Jordan
- Sales tax and applicable special tax treatments, registration and exemption evidence, local invoice requirements, returns and settlement.
- E-invoicing / national platform integration as required by the applicable taxpayer and current official specifications.
- Keep sales-tax concepts distinct from VAT assumptions in the shared engine.

### Egypt
- VAT, schedule tax and other applicable tax components, exemptions, zero-rating, withholding and import scenarios as legally applicable.
- Tax invoice/receipt profiles, taxpayer identifiers, required document fields, credit/debit notes and electronic receipt/invoice integration with the Egyptian Tax Authority.
- Versioned authority API, signing, UUID, status lifecycle, retries and archived request/response evidence.

### GCC beyond Saudi Arabia
- Separate country packs for Kuwait, UAE, Bahrain, Oman and Qatar.
- Do not assume GCC countries share a single VAT implementation or that a common rate applies. Support each country's legislative activation, registration, exemptions, invoice and reporting requirements independently.
- Include UAE corporate-tax support as a separate direct-tax module, not as a VAT component. Add other direct taxes only as jurisdiction-specific modules.

### Syria, Iraq and Lebanon
- Configurable VAT/sales/turnover, customs, excise, withholding and other applicable tax types based on current local rules.
- Country-specific registration, tax invoice, exemptions, returns, currency and rounding rules.
- Local authority/e-invoice integrations only after official technical specifications and operational requirements are verified.

### European Union and Europe
- VAT registrations by member state, standard/reduced/super-reduced/zero/exempt/out-of-scope treatments as applicable.
- Place-of-supply and B2B/B2C rules, VAT ID validation integration, reverse charge, intra-community supplies/acquisitions, imports/exports, distance sales/OSS/IOSS where applicable.
- Country-specific invoice fields, numbering, correction documents, fiscalization/e-reporting/e-invoicing adapters and filing formats.
- Country-specific VAT return mapping; never treat the EU as one rate table or one invoice schema.

### United States
- Sales and use tax by state and local jurisdiction, including layered rates, product/service taxability, sourcing, destination/origin rules, nexus, thresholds, exemption/resale certificates and effective-dated boundaries.
- Tax holidays, special districts, marketplace facilitator treatment and shipping/handling rules where applicable.
- Support federal, state and local tax reporting as separate capabilities. Federal income/payroll taxes must be separate modules from transaction sales tax.
- Provide an adapter contract for trusted tax-rate/jurisdiction providers; offline calculation must use a dated, traceable local dataset.

### Other jurisdictions
- A documented country-pack SDK/contract with validation, deterministic calculation, effective-dated datasets, posting profiles, localization, return mapping and conformance tests.
- Country packs must be independently versioned and upgradeable without changing historical documents.

## Architecture and repository placement

- Keep tax calculation and determination in domain/application services with provider-neutral interfaces.
- Keep all table DDL and migrations under `dba`; runtime services must not create or alter tax tables.
- Use separate repository/data-access code for SQLite and PostgreSQL with identical behavior and migration parity.
- Keep authority integrations in isolated adapters with secret storage outside source control and outside ordinary tax master tables.
- Do not mix tax calculation with `InvoiceWin` UI code or the current monolithic `TaxSvc`. The UI selects/overrides only permitted tax treatments and displays a transparent breakdown.
- Add English resource keys as the default UI language. All labels, validation and errors must resolve through localization resources.
- Enforce company isolation, role permissions, audit logging, period locks and approval controls in services, not only in UI.
- No credentials, private keys, taxpayer secrets or authority tokens may be committed to Git.

## Delivery stages

1. **Tax foundation:** domain model, jurisdiction/registration master, effective-dated tax codes, tax determination API, deterministic calculator and migration parity.
2. **Accounting integration:** line/document tax breakdown, input/output/recoverable/non-recoverable accounts, posting snapshots, returns/reversals and GL reconciliation.
3. **GCC/Saudi pack:** Saudi VAT and ZATCA adapter foundation, then separate UAE, Bahrain, Oman, Kuwait and Qatar packs according to verified legal applicability.
4. **Jordan and Egypt packs:** tax categories, invoice profiles, returns and official e-invoicing integrations.
5. **Syria, Iraq and Lebanon packs:** country-specific tax types and document/return workflows after rule verification.
6. **EU packs:** shared VAT primitives plus country-level rules and reporting/e-invoice adapters.
7. **US pack:** jurisdiction sourcing, layered sales/use tax, nexus, exemptions and taxability datasets.
8. **Additional countries:** country-pack SDK and staged rollout with local validation.

Each stage is independently tested and releasable; a country pack is not marked complete merely because a default rate is populated.

## Mandatory acceptance tests

- Rate/rule effective-date boundaries, retroactive corrections and historical snapshot immutability.
- Standard, reduced, zero, exempt, out-of-scope, reverse-charge, withholding and non-recoverable tax scenarios.
- Inclusive/exclusive prices, multiple components, compound taxes, discounts, freight, deposits, partial returns and credit/debit notes.
- Multi-currency calculations, exchange-rate snapshots, decimal precision and jurisdiction-specific rounding.
- US multi-layer jurisdiction calculations and exemption/nexus/sourcing cases.
- EU domestic, cross-border B2B/B2C, reverse-charge and import/export scenarios.
- Saudi and Egypt e-invoice lifecycle tests using official schemas and sandbox responses where available.
- Tax-to-GL, tax-to-party-ledger, tax-return-box and settlement reconciliation.
- Duplicate-post prevention, concurrency, rollback, retry, cancellation and reversal.
- Company isolation, permissions, audit trail, period lock and secrets handling.
- Identical functional results on SQLite and PostgreSQL.
- Regression tests proving that changing a tax rate never changes an already-posted document.

## Compliance governance

For every jurisdiction pack, retain a source register containing official legislation, tax authority guidance, technical schema versions, effective dates, retrieval/review dates and the reviewer/approval state. Revalidate before each release and after legal or authority updates. Clearly distinguish implemented, tested, authority-certified (where applicable) and legally reviewed status. Never advertise legal compliance or certification without evidence.
