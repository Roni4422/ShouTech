# Tax Engine Implementation Status

Branch: `work/full-app-stabilization-2026-11-18`

## Implemented in the current foundation increment

- Added a country/jurisdiction catalog for Saudi Arabia, Jordan, Egypt, Kuwait, UAE, Qatar, Bahrain, Oman, Syria, Iraq, Lebanon, the United States, all 27 EU member states, the United Kingdom and Türkiye.
- Added an explicit company-setup opt-in. The checkbox is unchecked by default; country selection alone does not enable tax.
- The selected country must resolve to a known ISO country code before an opt-in can be saved.
- Company tax activation is persisted in the company creation transaction for both SQLite and PostgreSQL. If the user opts in but the database write fails, company creation is refused rather than silently saving an untaxed company record.
- Added company-scoped tax settings and effective-dated tax-rule tables in migration 067 for SQLite and PostgreSQL. No rate rows are seeded by this migration.
- Changed tax lookup to fail closed: missing settings, missing rules, an unavailable database or an exception produces a zero default tax rate, never a guessed 15% rate.
- Material tax rates are suppressed when the company has not enabled its tax system.
- Invoice tax input is read-only when tax is disabled, and invoice capture forces all new line tax rates to zero in that state.
- Added a deterministic decimal calculator supporting multiple tax components, tax-inclusive and tax-exclusive prices, compound components, recoverability flags and configurable currency precision.
- Added unit tests for country catalog coverage and core calculation cases.

## Not yet complete — do not advertise these countries as fully tax-compliant

The catalog is a jurisdiction registry, not proof of a complete legal tax pack. The following work remains before a country can be marked production-supported:

- Verified, effective-dated country tax tables, including reduced, zero-rated, exempt, out-of-scope, special/schedule, withholding, excise and reverse-charge rules where applicable.
- Registration status, thresholds, registration-number validation, filing frequency and local rounding policies.
- Tax account mapping, tax ledger, invoice posting, returns, settlements and reconciliation with the general ledger.
- Country-specific invoice requirements and authority/e-invoicing adapters, including Saudi ZATCA and Egypt ETA.
- EU country-specific VAT returns, cross-border place-of-supply rules, OSS/IOSS and country e-invoicing/fiscalization.
- United States state, county, city and special-district rates, product taxability, sourcing, nexus, exemption certificates and tax holidays.
- Current legal review for Syria, Iraq, Kuwait, Qatar and any other jurisdiction whose applicable indirect-tax framework is not verified.
- SQLite/PostgreSQL integration tests for tax activation, tax rule persistence, invoice calculation, posting, reversal and historical snapshots.

## Important safety behavior

Tax is disabled unless the user explicitly opts in during company setup or an authorized user enables it later. Disabling tax does not delete historical tax rules or posted tax data. A country with no verified active tax rule must not inherit another country's rate.

## Official source register to start country-pack validation

- Saudi Arabia: [ZATCA VAT overview](https://zatca.gov.sa/en/RulesRegulations/VAT/Pages/About-VAT.aspx) and [GCC Unified VAT Agreement](https://www.zatca.gov.sa/en/RulesRegulations/Taxes/Pages/VATUnifiedAgreement.aspx).
- Jordan: [Income and Sales Tax Department — FAQ](https://www.istd.gov.jo/EN/Modules/faq) and [General Sales Tax tables](https://www.istd.gov.jo/EN/List/General__Sales_Tax_Tables).
- Egypt: [Egyptian Tax Authority — VAT Law No. 67 of 2016](https://www.eta.gov.eg/sites/default/files/2024-02/Law-english-no.67-2016.pdf.pdf) and [Executive Regulations](https://www.incometax.gov.eg/sites/default/files/2024-02/VAT-Executive-Regulations-English.pdf).
- United Arab Emirates: [Federal Tax Authority VAT FAQ](https://tax.gov.ae/en/faq.aspx?keyword=What+is+the+standard+rate+of+VAT+in+the+UAE%3F).
- Bahrain: [Bahrain Government VAT overview](https://www.bahrain.bh/new/en/business-vat_en.html).
- Oman: [Oman Tax Authority — tax rates](https://tms.taxoman.gov.om/portal/tax-rate) and [VAT law and regulations](https://tms.taxoman.gov.om/portal/vat-law-regulations).
- Lebanon: [Ministry of Finance — VAT rate](https://finance.gov.lb/en-us/Taxation/Individuals/VAT/Pages/Tax-Rate.aspx). The official page currently states 10%; older secondary sources may show 11%, so the official current source must prevail unless a later legal amendment is found.
- European Union: [European Commission — VAT rates](https://taxation-customs.ec.europa.eu/taxation/vat/vat-directive/vat-rates_en) and [country-specific VAT information](https://taxation-customs.ec.europa.eu/taxation/vat/vat-directive/vat-rates/country-specific-information-vat_en).

Source register entries are research starting points, not legal sign-off. Each tax pack requires dated source review before release.
