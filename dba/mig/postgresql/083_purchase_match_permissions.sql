-- 083 - Dedicated permissions for purchase match policy and exception release.
-- Only administrators receive these sensitive permissions by default.
CREATE UNIQUE INDEX IF NOT EXISTS ux_purchase_match_override_evaluation
    ON purchase_match_overrides(company_code, invoice_doc_id, evaluation_id);

INSERT INTO permissions
    (permission_code, permission_name, group_code, feature_code, operation_code,
     name_ar, name_en, description, display_order, is_sensitive)
VALUES
    ('PURCH.MATCH.POLICY.MANAGE', 'Purchasing - Manage Three-Way Match Policy',
     'PURCHASES', 'PURCH.MATCH', 'POLICY.MANAGE',
     'Purchasing - Manage Three-Way Match Policy',
     'Purchasing - Manage Three-Way Match Policy',
     'Configure company purchase invoice matching thresholds', 342, true),
    ('PURCH.MATCH.OVERRIDE', 'Purchasing - Release Blocked Match',
     'PURCHASES', 'PURCH.MATCH', 'OVERRIDE',
     'Purchasing - Release Blocked Match',
     'Purchasing - Release Blocked Match',
     'Release a blocked supplier invoice match with a mandatory audit reason', 343, true)
ON CONFLICT(permission_code) DO UPDATE SET
    permission_name=EXCLUDED.permission_name,
    group_code=EXCLUDED.group_code,
    feature_code=EXCLUDED.feature_code,
    operation_code=EXCLUDED.operation_code,
    name_ar=EXCLUDED.name_ar,
    name_en=EXCLUDED.name_en,
    description=EXCLUDED.description,
    display_order=EXCLUDED.display_order,
    is_sensitive=EXCLUDED.is_sensitive,
    is_active=true;

INSERT INTO role_permissions(role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=true AND p.is_active=true
  AND p.permission_code IN ('PURCH.MATCH.POLICY.MANAGE','PURCH.MATCH.OVERRIDE')
ON CONFLICT DO NOTHING;
