ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS reversal_of_entry_id uuid NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_reversal_of
    ON journal_entries(reversal_of_entry_id)
    WHERE reversal_of_entry_id IS NOT NULL;
