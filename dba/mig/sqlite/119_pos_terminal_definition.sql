-- 119 - POS terminal definition completion (POS family P1,
-- owner workstream 2026-11-24 15:31). pos_terminals (036)
-- carried only code, names, warehouse and the cash account.
-- A real terminal definition (Al-Ameen / Phenix / ERPNext POS
-- profile pattern) also binds the card/bank account, the cash
-- drawer, the receipt printer, the walk-in default customer,
-- the price level the till sells from, and the users permitted
-- to open sessions on the terminal.

-- @if-column-missing pos_terminals.bank_account_code
ALTER TABLE pos_terminals ADD COLUMN bank_account_code TEXT;
-- @if-column-missing pos_terminals.drawer_name
ALTER TABLE pos_terminals ADD COLUMN drawer_name TEXT;
-- @if-column-missing pos_terminals.printer_name
ALTER TABLE pos_terminals ADD COLUMN printer_name TEXT;
-- @if-column-missing pos_terminals.default_customer_code
ALTER TABLE pos_terminals ADD COLUMN default_customer_code TEXT;
-- @if-column-missing pos_terminals.price_level
ALTER TABLE pos_terminals ADD COLUMN price_level TEXT;
-- @if-column-missing pos_terminals.permitted_users
ALTER TABLE pos_terminals ADD COLUMN permitted_users TEXT;
