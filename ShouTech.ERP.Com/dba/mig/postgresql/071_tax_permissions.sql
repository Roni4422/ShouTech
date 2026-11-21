-- 071 - Tax configuration and reporting permissions.
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.VIEW','Tax Configuration - View','ACCOUNTING','TAX','VIEW','Tax configuration access','Tax Configuration - View','View the company tax jurisdiction, rates, and tax accounts.',351,false)
ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.MANAGE','Tax Configuration - Manage','ACCOUNTING','TAX','MANAGE','Tax configuration management','Tax Configuration - Manage','Create and maintain effective-dated tax rules and account mappings.',352,true)
ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.REPORT.VIEW','Tax Reports - View','ACCOUNTING','TAX','REPORT_VIEW','Tax report access','Tax Reports - View','View tax transaction registers and tax period summaries.',353,false)
ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=true AND p.is_active=true
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.MANAGE','ACC.TAX.REPORT.VIEW') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND r.is_active=true AND p.is_active=true
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND r.is_active=true AND p.is_active=true
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW') ON CONFLICT DO NOTHING;
