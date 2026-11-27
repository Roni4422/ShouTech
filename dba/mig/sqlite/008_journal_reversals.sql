ALTER TABLE journal_entries ADD COLUMN reversal_of_entry_id TEXT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_reversal_of
    ON journal_entries(reversal_of_entry_id)
    WHERE reversal_of_entry_id IS NOT NULL;
