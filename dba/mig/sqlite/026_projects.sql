-- Owns the Projects module (projects + project tasks). Dates are stored as
-- ISO text (yyyy-MM-dd) on both providers, matching the commercial-document
-- convention used by the provider-neutral repositories.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS projects (
    project_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    project_code TEXT NOT NULL,
    project_name TEXT NOT NULL,
    customer_id INTEGER,
    customer_name TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Active','OnHold','Completed','Cancelled')),
    start_date TEXT,
    end_date TEXT,
    budget NUMERIC NOT NULL DEFAULT 0 CHECK (budget >= 0),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_company_code
    ON projects(company_id, project_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS project_tasks (
    task_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id),
    task_name TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'ToDo'
        CHECK (status IN ('ToDo','InProgress','Done')),
    due_date TEXT,
    assigned_to TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_project_tasks_project
    ON project_tasks(company_id, project_id);
