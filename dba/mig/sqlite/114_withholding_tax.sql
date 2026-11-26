-- 114 - Withholding tax / TDS (SQLite, W1). Withholding is a first-class
-- category of the EXISTING tax engine (migrations 067-091): a company tax
-- rule may be marked as a withholding rule, carrying its own rate, the
-- withholding PAYABLE (liability) control account it credits, and an
-- optional threshold. No country rates are seeded: every value is
-- configured per company / jurisdiction, exactly like the VAT rules.
-- PostgreSQL counterpart: postgresql/114_withholding_tax.sql.
--
-- Model (ERPNext invoice-time TDS): when a posted supplier invoice
-- carries a withholding rule, the invoice posts AP control 2201 GROSS,
-- then a deduction leg debits 2201 and credits the withholding payable
-- account, so the supplier's party-ledger balance and the document's
-- allocatable open amount are NET (gross - withheld). The withheld
-- amount is registered in tax_transactions with tax_type 'WITHHOLDING'
-- (direction INPUT, line_no 0) so the tax register is the single source
-- for withholding reporting / certificates.
--
-- Withholding base: the invoice base EXCLUDING VAT (document total
-- minus its tax amount on the manual path; the lines subtotal on the
-- chain path) - withholding is charged on the consideration for the
-- supply, never on the VAT the supplier collects for the authority.
-- Threshold modes: 'PerDocument' (the base of this one invoice must
-- reach the threshold) or 'Cumulative' (the supplier's calendar-year
-- withholding bases including this invoice must cross the threshold;
-- withholding then applies only to the excess over the threshold).

-- Withholding category on the existing tax-code model.
-- @if-column-missing company_tax_rules.is_withholding
ALTER TABLE company_tax_rules ADD COLUMN is_withholding INTEGER NOT NULL DEFAULT 0 CHECK (is_withholding IN (0, 1));
-- @if-column-missing company_tax_rules.withholding_account_code
ALTER TABLE company_tax_rules ADD COLUMN withholding_account_code TEXT;
-- @if-column-missing company_tax_rules.withholding_threshold_amount
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_amount NUMERIC;
-- @if-column-missing company_tax_rules.withholding_threshold_mode
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_mode TEXT NOT NULL DEFAULT 'PerDocument';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_withholding
    ON company_tax_rules(company_code, is_withholding, is_active);

-- Supplier-level default withholding code (party model default; the
-- invoice-level selection on purchase_documents wins over it, and the
-- company's default withholding rule is the final fallback).
-- @if-column-missing parties.withholding_tax_code
ALTER TABLE parties ADD COLUMN withholding_tax_code TEXT;

-- Persisted withholding outcome on the supplier invoice header: the
-- code/rate/base/amount/account actually applied at posting (0 amount
-- with a code set = evaluated, below threshold). payment allocation
-- settles grand_total - withholding_amount - paid_amount.
-- @if-column-missing purchase_documents.withholding_tax_code
ALTER TABLE purchase_documents ADD COLUMN withholding_tax_code TEXT;
-- @if-column-missing purchase_documents.withholding_rate
ALTER TABLE purchase_documents ADD COLUMN withholding_rate NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_base
ALTER TABLE purchase_documents ADD COLUMN withholding_base NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_amount
ALTER TABLE purchase_documents ADD COLUMN withholding_amount NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_account_code
ALTER TABLE purchase_documents ADD COLUMN withholding_account_code TEXT;

CREATE INDEX IF NOT EXISTS ix_purchase_documents_withholding
    ON purchase_documents(company_id, party_id, doc_date) WHERE withholding_amount > 0;
CREATE INDEX IF NOT EXISTS ix_tax_transactions_withholding
    ON tax_transactions(company_code, tax_type, document_date, source_doc_id);

-- Withholding Tax Payable account (2215), the DEFAULT liability account
-- a withholding rule may point at (the rule's configured account is
-- authoritative; 2215 exists so the mechanism functions out of the box
-- and the payable is separately reportable). Seeded into every existing
-- company that carries AP control (2201), under 2201's parent in the
-- 22xx current-liability block (2201 AP, 2203 GRNI, 2205 Output VAT,
-- 2211 Payroll Taxes; 2215 is free in every template). New companies
-- receive it from the COA templates (cod/app/src/Resources/COA/*.shc).
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '2215',
       'Withholding Tax Payable',
       'Withholding Tax Payable',
       'LIABILITY',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '2201'),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '2201')
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts a
                  WHERE a.company_id = c.company_id AND a.account_code = '2215');
