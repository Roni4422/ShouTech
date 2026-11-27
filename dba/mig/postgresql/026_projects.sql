-- PostgreSQL counterpart of sqlite/026_projects.sql (Projects module).

CREATE TABLE IF NOT EXISTS projects (
    project_id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    project_code TEXT NOT NULL,
    project_name TEXT NOT NULL,
    customer_id BIGINT,
    customer_name TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Active','OnHold','Completed','Cancelled')),
    start_date TEXT,
    end_date TEXT,
    budget NUMERIC NOT NULL DEFAULT 0 CHECK (budget >= 0),
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_company_code
    ON projects(company_id, project_code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS project_tasks (
    task_id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    project_id uuid NOT NULL REFERENCES projects(project_id),
    task_name TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'ToDo'
        CHECK (status IN ('ToDo','InProgress','Done')),
    due_date TEXT,
    assigned_to TEXT,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_project_tasks_project
    ON project_tasks(company_id, project_id);
