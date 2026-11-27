-- Enterprise RBAC baseline. Administrator is the highest customer role.
INSERT INTO permissions (permission_code, permission_name) VALUES ('COMPANY_VIEW', 'View companies') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('COMPANY_CREATE', 'Create companies') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('COMPANY_EDIT', 'Edit companies') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('COMPANY_DELETE', 'Delete companies') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('USER_VIEW', 'View users') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('USER_CREATE', 'Create users') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('USER_EDIT', 'Edit users') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('USER_DELETE', 'Delete users') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('ROLE_VIEW', 'View roles') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('ROLE_EDIT', 'Manage roles') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('PERMISSION_VIEW', 'View permissions') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('SETTINGS_EDIT', 'Manage settings') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('BACKUP_RUN', 'Run backups') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('BACKUP_RESTORE', 'Restore backups') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('ACCOUNTING_VIEW', 'View accounting') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('ACCOUNTING_EDIT', 'Manage accounting') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('JOURNAL_POST', 'Post journal entries') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('INVENTORY_VIEW', 'View inventory') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('INVENTORY_EDIT', 'Manage inventory') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('SALES_VIEW', 'View sales') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('SALES_EDIT', 'Manage sales') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('PURCHASE_VIEW', 'View purchases') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('PURCHASE_EDIT', 'Manage purchases') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('REPORT_VIEW', 'View reports') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('AUDIT_VIEW', 'View audit log') ON CONFLICT (permission_code) DO NOTHING;
INSERT INTO permissions (permission_code, permission_name) VALUES ('SYSTEM_VIEW', 'View system status') ON CONFLICT (permission_code) DO NOTHING;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = TRUE AND p.is_active = TRUE
ON CONFLICT DO NOTHING;
