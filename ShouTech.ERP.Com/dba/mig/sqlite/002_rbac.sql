-- Enterprise RBAC baseline. Administrator is the highest customer role.
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('COMPANY_VIEW', 'View companies');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('COMPANY_CREATE', 'Create companies');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('COMPANY_EDIT', 'Edit companies');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('COMPANY_DELETE', 'Delete companies');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('USER_VIEW', 'View users');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('USER_CREATE', 'Create users');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('USER_EDIT', 'Edit users');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('USER_DELETE', 'Delete users');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('ROLE_VIEW', 'View roles');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('ROLE_EDIT', 'Manage roles');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('PERMISSION_VIEW', 'View permissions');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('SETTINGS_EDIT', 'Manage settings');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('BACKUP_RUN', 'Run backups');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('BACKUP_RESTORE', 'Restore backups');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('ACCOUNTING_VIEW', 'View accounting');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('ACCOUNTING_EDIT', 'Manage accounting');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('JOURNAL_POST', 'Post journal entries');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('INVENTORY_VIEW', 'View inventory');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('INVENTORY_EDIT', 'Manage inventory');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('SALES_VIEW', 'View sales');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('SALES_EDIT', 'Manage sales');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('PURCHASE_VIEW', 'View purchases');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('PURCHASE_EDIT', 'Manage purchases');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('REPORT_VIEW', 'View reports');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('AUDIT_VIEW', 'View audit log');
INSERT OR IGNORE INTO permissions (permission_code, permission_name) VALUES ('SYSTEM_VIEW', 'View system status');

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1;
