-- 071 - Tax configuration and reporting permissions.
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.VIEW','Tax Configuration - View','ACCOUNTING','TAX','VIEW','عرض إعدادات الضرائب','Tax Configuration - View','View the company tax jurisdiction, rates, and tax accounts.',351,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TAX',operation_code='VIEW',name_en='Tax Configuration - View',display_order=351,is_sensitive=0 WHERE permission_code='ACC.TAX.VIEW';

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.MANAGE','Tax Configuration - Manage','ACCOUNTING','TAX','MANAGE','إدارة إعدادات الضرائب','Tax Configuration - Manage','Create and maintain effective-dated tax rules and account mappings.',352,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TAX',operation_code='MANAGE',name_en='Tax Configuration - Manage',display_order=352,is_sensitive=1 WHERE permission_code='ACC.TAX.MANAGE';

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive)
VALUES ('ACC.TAX.REPORT.VIEW','Tax Reports - View','ACCOUNTING','TAX','REPORT_VIEW','عرض التقارير الضريبية','Tax Reports - View','View tax transaction registers and tax period summaries.',353,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TAX',operation_code='REPORT_VIEW',name_en='Tax Reports - View',display_order=353,is_sensitive=0 WHERE permission_code='ACC.TAX.REPORT.VIEW';

INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_ADMINISTRATOR' AND r.is_active=1 AND p.is_active=1
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.MANAGE','ACC.TAX.REPORT.VIEW');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND r.is_active=1 AND p.is_active=1
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND r.is_active=1 AND p.is_active=1
AND p.permission_code IN ('ACC.TAX.VIEW','ACC.TAX.REPORT.VIEW');
