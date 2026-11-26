CREATE TABLE IF NOT EXISTS outbound_messages (
    outbound_message_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT,
    channel TEXT NOT NULL CHECK (channel IN ('WEBHOOK', 'EMAIL', 'SMS')),
    recipient_address TEXT NOT NULL,
    subject TEXT,
    body TEXT NOT NULL,
    body_format TEXT NOT NULL DEFAULT 'TEXT',
    status TEXT NOT NULL DEFAULT 'QUEUED' CHECK (status IN ('QUEUED', 'SENDING', 'SENT', 'FAILED')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    max_attempts INTEGER NOT NULL DEFAULT 6 CHECK (max_attempts > 0),
    next_attempt_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    sent_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_outbound_messages_due
    ON outbound_messages(status, next_attempt_at, outbound_message_id);
