-- Enterprise role model: Administrator, User, Viewer only.
-- Existing Manager/Accountant assignments are normalized to User before the legacy roles are removed.

INSERT INTO roles (company_id, role_code, role_name)
SELECT company_id, 'ROLE_USER', 'User' FROM companies
ON CONFLICT (company_id, role_code) DO NOTHING;

INSERT INTO roles (company_id, role_code, role_name)
SELECT company_id, 'ROLE_VIEWER', 'Viewer' FROM companies
ON CONFLICT (company_id, role_code) DO NOTHING;

INSERT INTO user_roles (user_id, role_id)
SELECT ur.user_id, r_user.role_id
FROM user_roles ur
INNER JOIN roles old_role ON old_role.role_id = ur.role_id
INNER JOIN users u ON u.user_id = ur.user_id
INNER JOIN roles r_user ON r_user.company_id = u.company_id AND r_user.role_code = 'ROLE_USER'
WHERE old_role.role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT')
ON CONFLICT DO NOTHING;

DELETE FROM role_permissions
WHERE role_id IN (SELECT role_id FROM roles WHERE role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT'));

DELETE FROM user_roles
WHERE role_id IN (SELECT role_id FROM roles WHERE role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT'));

DELETE FROM roles
WHERE role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT');

-- Baseline Viewer: read-only business visibility.
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER'
  AND p.permission_code IN ('COMPANY_VIEW','USER_VIEW','ROLE_VIEW','PERMISSION_VIEW',
                            'ACCOUNTING_VIEW','INVENTORY_VIEW','SALES_VIEW','PURCHASE_VIEW',
                            'REPORT_VIEW','SYSTEM_VIEW')
  AND p.is_active = TRUE
ON CONFLICT DO NOTHING;

-- Baseline User: business operations, without security/role administration.
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER'
  AND p.permission_code IN ('COMPANY_VIEW','ACCOUNTING_VIEW','ACCOUNTING_EDIT','JOURNAL_POST',
                            'INVENTORY_VIEW','INVENTORY_EDIT','SALES_VIEW','SALES_EDIT',
                            'PURCHASE_VIEW','PURCHASE_EDIT','REPORT_VIEW')
  AND p.is_active = TRUE
ON CONFLICT DO NOTHING;

-- Administrator remains the only built-in role with full current permissions.
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = TRUE AND p.is_active = TRUE
ON CONFLICT DO NOTHING;
