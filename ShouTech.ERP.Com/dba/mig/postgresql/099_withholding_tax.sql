-- 099 - Withholding tax / TDS (PostgreSQL, W1). See the SQLite
-- companion for the design note: withholding as a category of the
-- existing tax engine, deducted at supplier-invoice posting, AP gross
-- with a deduction leg to the withholding payable account, supplier
-- balance and allocation NET, tax_transactions as the only register.

-- @if-column-missing company_tax_rules.is_withholding
ALTER TABLE company_tax_rules ADD COLUMN is_withholding BOOLEAN NOT NULL DEFAULT FALSE;
-- @if-column-missing company_tax_rules.withholding_account_code
ALTER TABLE company_tax_rules ADD COLUMN withholding_account_code VARCHAR(50);
-- @if-column-missing company_tax_rules.withholding_threshold_amount
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_amount NUMERIC(20,6);
-- @if-column-missing company_tax_rules.withholding_threshold_mode
ALTER TABLE company_tax_rules ADD COLUMN withholding_threshold_mode VARCHAR(20) NOT NULL DEFAULT 'PerDocument';

CREATE INDEX IF NOT EXISTS ix_company_tax_rules_withholding
    ON company_tax_rules(company_code, is_withholding, is_active);

-- @if-column-missing parties.withholding_tax_code
ALTER TABLE parties ADD COLUMN withholding_tax_code VARCHAR(50);

-- @if-column-missing purchase_documents.withholding_tax_code
ALTER TABLE purchase_documents ADD COLUMN withholding_tax_code VARCHAR(50);
-- @if-column-missing purchase_documents.withholding_rate
ALTER TABLE purchase_documents ADD COLUMN withholding_rate NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_base
ALTER TABLE purchase_documents ADD COLUMN withholding_base NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_amount
ALTER TABLE purchase_documents ADD COLUMN withholding_amount NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing purchase_documents.withholding_account_code
ALTER TABLE purchase_documents ADD COLUMN withholding_account_code VARCHAR(50);

CREATE INDEX IF NOT EXISTS ix_purchase_documents_withholding
    ON purchase_documents(company_id, party_id, doc_date) WHERE withholding_amount > 0;
CREATE INDEX IF NOT EXISTS ix_tax_transactions_withholding
    ON tax_transactions(company_code, tax_type, document_date, source_doc_id);

-- Withholding Tax Payable (2215): see the SQLite companion.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT md5(c.company_id::text || ':099:' || '2215')::uuid,
       c.company_id,
       '2215',
       'Withholding Tax Payable',
       'Withholding Tax Payable',
       'LIABILITY',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '2201'),
       TRUE, TRUE, FALSE
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '2201')
ON CONFLICT (company_id, account_code) DO NOTHING;
