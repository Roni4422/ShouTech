-- 118 - Account card completion (grid program R2e, owner spec
-- 2026-11-24 15:07/15:10). The account card is modelled on the
-- Al-Ameen 2004 chart/card reference (structure/content only;
-- visuals stay on the modern central system). chart_of_accounts
-- already carried code, names (Arabic + Latin), type, parent,
-- risk/confidentiality level, budget-control flag, reconcile
-- flag, tax rate, notes and computed balances. The fields the
-- reference card shows that the schema lacked are added here:
-- the closing account (posted to at year-end closing), the
-- balance warning side, the account's nature/side, the estimated
-- budget amount, the account currency + exchange parity, and
-- the last reconciliation date.

-- @if-column-missing chart_of_accounts.closing_account_id
ALTER TABLE chart_of_accounts ADD COLUMN closing_account_id UUID;
-- @if-column-missing chart_of_accounts.warning_side
ALTER TABLE chart_of_accounts ADD COLUMN warning_side VARCHAR(10) NOT NULL DEFAULT 'NONE';
-- @if-column-missing chart_of_accounts.account_side
ALTER TABLE chart_of_accounts ADD COLUMN account_side VARCHAR(10) NOT NULL DEFAULT 'BOTH';
-- @if-column-missing chart_of_accounts.estimated_budget
ALTER TABLE chart_of_accounts ADD COLUMN estimated_budget NUMERIC(20,6) NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.currency_code
ALTER TABLE chart_of_accounts ADD COLUMN currency_code VARCHAR(10);
-- @if-column-missing chart_of_accounts.exchange_parity
ALTER TABLE chart_of_accounts ADD COLUMN exchange_parity NUMERIC(20,6) NOT NULL DEFAULT 1;
-- @if-column-missing chart_of_accounts.reconciliation_date
ALTER TABLE chart_of_accounts ADD COLUMN reconciliation_date DATE;
