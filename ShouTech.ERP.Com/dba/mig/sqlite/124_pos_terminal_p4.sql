-- 124 - POS terminal definition P4 (docs/POS_FAMILY.md P4): the
-- Phenix-verified bindings ShouTech lacked - the document kinds the
-- point issues, a SEPARATE cashbox-closing account, per-point
-- currency, customer-group scoping, and per-point print designs
-- (names of doc_designs rows, migration 116). Plus the closing
-- account recorded on each day-close register row.

-- @if-column-missing pos_terminals.sales_doc_kind
ALTER TABLE pos_terminals ADD COLUMN sales_doc_kind TEXT;
-- @if-column-missing pos_terminals.closing_account_code
ALTER TABLE pos_terminals ADD COLUMN closing_account_code TEXT;
-- @if-column-missing pos_terminals.currency_code
ALTER TABLE pos_terminals ADD COLUMN currency_code TEXT;
-- @if-column-missing pos_terminals.customer_group_code
ALTER TABLE pos_terminals ADD COLUMN customer_group_code TEXT;
-- @if-column-missing pos_terminals.sale_design_name
ALTER TABLE pos_terminals ADD COLUMN sale_design_name TEXT;
-- @if-column-missing pos_terminals.return_design_name
ALTER TABLE pos_terminals ADD COLUMN return_design_name TEXT;
-- @if-column-missing pos_terminals.gift_design_name
ALTER TABLE pos_terminals ADD COLUMN gift_design_name TEXT;
-- @if-column-missing pos_cash_closings.closing_account_code
ALTER TABLE pos_cash_closings ADD COLUMN closing_account_code TEXT;
