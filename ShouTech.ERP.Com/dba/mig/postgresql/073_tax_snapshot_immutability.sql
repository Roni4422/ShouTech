-- 073 - Enforce immutability of posted tax snapshots (PostgreSQL).
CREATE OR REPLACE FUNCTION prevent_tax_transaction_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $tax_guard$
BEGIN
    RAISE EXCEPTION 'Posted tax snapshots are immutable; create a reversal document.';
END;
$tax_guard$;

DROP TRIGGER IF EXISTS trg_tax_transactions_no_update ON tax_transactions;
CREATE TRIGGER trg_tax_transactions_no_update
BEFORE UPDATE ON tax_transactions
FOR EACH ROW EXECUTE FUNCTION prevent_tax_transaction_mutation();

DROP TRIGGER IF EXISTS trg_tax_transactions_no_delete ON tax_transactions;
CREATE TRIGGER trg_tax_transactions_no_delete
BEFORE DELETE ON tax_transactions
FOR EACH ROW EXECUTE FUNCTION prevent_tax_transaction_mutation();
