-- 083 - Dedicated permissions for purchase match policy and exception release.
-- Only administrators receive these sensitive permissions by default.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_match_override_evaluation
    ON purchase_match_overrides(company_code, invoice_doc_id, evaluation_id);

INSERT OR IGNORE INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('PURCH.MATCH.POLICY.MANAGE', 'Purchasing - Manage Three-Way Match Policy',
     'PURCHASES', 'PURCH.MATCH', 'POLICY.MANAGE',
     'Purchasing - Manage Three-Way Match Policy',
     'Purchasing - Manage Three-Way Match Policy',
     'Configure company purchase invoice matching thresholds', 342, 1),
    ('PURCH.MATCH.OVERRIDE', 'Purchasing - Release Blocked Match',
     'PURCHASES', 'PURCH.MATCH', 'OVERRIDE',
     'Purchasing - Release Blocked Match',
     'Purchasing - Release Blocked Match',
     'Release a blocked supplier invoice match with a mandatory audit reason', 343, 1);

UPDATE permissions
SET group_code='PURCHASES', feature_code='PURCH.MATCH', operation_code='POLICY.MANAGE',
    name_ar='Purchasing - Manage Three-Way Match Policy',
    name_en='Purchasing - Manage Three-Way Match Policy',
    description='Configure company purchase invoice matching thresholds',
    display_order=342, is_sensitive=1, is_active=1
WHERE permission_code='PURCH.MATCH.POLICY.MANAGE';

UPDATE permissions
SET group_code='PURCHASES', feature_code='PURCH.MATCH', operation_code='OVERRIDE',
    name_ar='Purchasing - Release Blocked Match',
    name_en='Purchasing - Release Blocked Match',
    description='Release a blocked supplier invoice match with a mandatory audit reason',
    display_order=343, is_sensitive=1, is_active=1
WHERE permission_code='PURCH.MATCH.OVERRIDE';

INSERT OR IGNORE INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=1 AND p.is_active=1
  AND p.permission_code IN ('PURCH.MATCH.POLICY.MANAGE','PURCH.MATCH.OVERRIDE');
