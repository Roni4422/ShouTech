# ShouTech Engineering Reference Library

This is the curated external reference index for developers. Use it to understand established business processes, data concepts, security controls and integration contracts. The reference material informs ShouTech design; it does not override ShouTech's architecture or prove local legal compliance.

## Reference-use rules

- Prefer official documentation, standards bodies and maintained source repositories.
- Record the product/version and date when a benchmark claim may change.
- Compare workflows and acceptance behavior, not proprietary internal code.
- Do not copy licensed or proprietary code, text, artwork, schemas or branding without permission.
- Confirm tax, e-invoicing, payroll, privacy and payment requirements using the relevant official national authority.
- For each adopted concept, create a ShouTech design decision, implementation issue/PR, tests and documentation update.
- Re-check links and versions during each major roadmap review.

## ERP and business-process references

| Source | Useful areas | How ShouTech should use it |
|---|---|---|
| [ERPNext documentation](https://docs.frappe.io/erpnext) and [source](https://github.com/frappe/erpnext) | Accounting entries, selling, buying, stock, manufacturing, assets, projects, HR and workflows | Study end-to-end document lifecycles, posting effects, configuration and operational controls |
| [Odoo documentation](https://www.odoo.com/documentation/) and [source](https://github.com/odoo/odoo) | Accounting, inventory, POS, CRM, MRP, approvals and integrations | Compare user journeys, modular boundaries, validation and extension points |
| [Microsoft Dynamics 365 documentation](https://learn.microsoft.com/dynamics365/) | Finance, supply chain, commerce, sales, security and enterprise operations | Benchmark enterprise controls, workflows, role separation and integration design |
| [SAP Help Portal](https://help.sap.com/) | S/4HANA finance, procurement, manufacturing, inventory and analytics | Reference complex enterprise process controls and master-data governance |
| [Manager.io guides](https://www.manager.io/guides/) | General ledger, receivables, payables, inventory, manufacturing, tax and reports | Benchmark accounting clarity, simple workflows and report usability |
| [Dolibarr documentation](https://wiki.dolibarr.org/) and [source](https://github.com/Dolibarr/dolibarr) | ERP/CRM modules, commercial documents, stock and extensions | Compare practical modular ERP/CRM behavior and extension patterns |

## Accounting, reporting and data standards

| Standard/source | Scope | Application |
|---|---|---|
| [IFRS Foundation](https://www.ifrs.org/issued-standards/list-of-standards/) | International financial reporting standards | Accounting policy research; use licensed/official material and jurisdiction adoption status |
| [IFRS jurisdiction profiles](https://www.ifrs.org/use-around-the-world/use-of-ifrs-standards-by-jurisdiction/) | Country adoption of IFRS | Determine which accounting framework applies; do not assume universal adoption |
| [XBRL International](https://www.xbrl.org/) | Structured business and financial reporting | Consider future structured reporting/export requirements |
| [GS1 standards](https://www.gs1.org/standards) | Product identifiers, barcodes, traceability | Barcode formats, identifiers and supply-chain interoperability |
| [ISO](https://www.iso.org/standards.html) | Quality, security, risk and management standards | Use relevant standards as requirements inputs; verify editions and licensing |

## Security, identity and payment references

| Source | Scope | Application |
|---|---|---|
| [OWASP ASVS](https://owasp.org/www-project-application-security-verification-standard/) | Application security verification | Security acceptance checklist and release review |
| [OWASP API Security Top 10](https://owasp.org/www-project-api-security/) | API threats | API authorization, object-level access, rate limits and token controls |
| [NIST Cybersecurity Framework](https://www.nist.gov/cyberframework) | Security risk management | Organize governance, protection, detection, response and recovery |
| [PCI Security Standards Council](https://www.pcisecuritystandards.org/) | Payment-card data security | POS/payment integration scope and vendor responsibility; avoid storing card data unnecessarily |
| [OAuth 2.0 Security Best Current Practice](https://www.rfc-editor.org/rfc/rfc9700) | Authorization security | Token flows and secure API integration where OAuth is appropriate |

## AI and machine-learning references

| Source | Scope | Application |
|---|---|---|
| [OpenAI developer documentation](https://platform.openai.com/docs/) | Model APIs, structured outputs, tool/function calling and agents | Build bounded assistants and approved service actions; review current API versions |
| [NIST AI Risk Management Framework](https://www.nist.gov/itl/ai-risk-management-framework) | AI risk governance | Document intended use, risks, evaluation, monitoring and human oversight |
| [OWASP Top 10 for LLM Applications](https://owasp.org/www-project-top-10-for-large-language-model-applications/) | Prompt injection, data leakage and unsafe tool use | Secure retrieval, tool permissions, data boundaries and action confirmation |

## Country-specific legal and tax research

Country packages must be based on current primary sources, not generic ERP defaults. Examples of official starting points include:

- Saudi Arabia: [ZATCA](https://zatca.gov.sa/) — VAT, e-invoicing (FATOORA), registration and technical specifications.
- Jordan: [Income and Sales Tax Department](https://istd.gov.jo/) — sales tax, income tax and e-invoicing requirements.
- Egypt: [Egyptian Tax Authority](https://www.eta.gov.eg/) — VAT and electronic invoicing/receipt systems.
- United Arab Emirates: [Federal Tax Authority](https://tax.gov.ae/) — VAT and corporate tax.
- Kuwait: [Ministry of Finance](https://www.mof.gov.kw/) — verify current tax and business requirements with official publications.
- United States: [Internal Revenue Service](https://www.irs.gov/) and relevant state revenue departments — federal and state requirements differ.
- European Union: [European Commission Taxation and Customs Union](https://taxation-customs.ec.europa.eu/) — VAT framework; member-state rules and e-invoicing requirements must be checked individually.
- United Kingdom: [HM Revenue & Customs](https://www.gov.uk/government/organisations/hm-revenue-customs) — VAT and Making Tax Digital requirements.
- Türkiye: [Revenue Administration](https://www.gib.gov.tr/) — tax and e-document requirements.

This list is not exhaustive and is not legal advice. Every country package must record official source URL, source publication/effective date, applicable taxpayer type, jurisdiction scope, evidence status, reviewer and next review date. Never enable a country's tax package merely because the company selected that country.

## Research record template

For each adopted reference, record:

- Topic/module:
- Source organization and official URL:
- Document/product version:
- Publication or effective date:
- Date reviewed:
- Relevant workflow or requirement:
- ShouTech design decision:
- Affected code/schema/UI:
- Tests and acceptance evidence:
- Legal/compliance reviewer if applicable:
- Next review date:
