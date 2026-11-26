-- 073 - Enforce immutability of posted tax snapshots (SQLite).
CREATE TRIGGER IF NOT EXISTS trg_tax_transactions_no_update
BEFORE UPDATE ON tax_transactions
BEGIN
    SELECT RAISE(ABORT, 'Posted tax snapshots are immutable; create a reversal document.');
END;

CREATE TRIGGER IF NOT EXISTS trg_tax_transactions_no_delete
BEFORE DELETE ON tax_transactions
BEGIN
    SELECT RAISE(ABORT, 'Posted tax snapshots are immutable; create a reversal document.');
END;
