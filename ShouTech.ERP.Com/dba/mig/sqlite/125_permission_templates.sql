-- ============================================================================
-- 125 - Permission templates + catalog reconciliation + DOC family (SQLite)
--
-- Permissions workstream (docs/PERMISSIONS_STUDY.md sections 2.1-2.3):
--   1. permission_groups gains the families the catalog already uses but
--      PrmWin could not show: HR, QUALITY, ASSETS, PROJECTS, plus the new
--      DOCUMENTS group; the stray PURCHASES group code on permission rows
--      is normalized to PURCHASING.
--   2. The 14 PermCat codes that had no database row are seeded (the
--      code-to-database drift of study section 1.1), including
--      SYS.PERMISSION_TEMPLATES.MANAGE, which governs this feature.
--   3. The per-document-type family DOC.<KIND>.<OP> (13 kinds x 8
--      operations = 104 keys) in the DOCUMENTS group. POST / DELETE /
--      FIELDS / ACCOUNT are sensitive.
--   4. Role-baseline mirroring: a role holding a generic document key
--      receives the matching DOC keys (what the SQLite bootstrapper
--      derives automatically at company open; PostgreSQL has no
--      bootstrapper, so the migration states it explicitly - and the
--      same statements keep custom SQLite roles consistent).
--   5. Template tables: permission_templates (company_id NULL/empty =
--      global system template), permission_template_grants,
--      user_permission_templates. Templates resolve as ONE FLAT UNION
--      in the permission loader (study section 2.2).
--   6. Three seeded global system templates (immutable through the
--      service; Copy makes an editable company template).
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, seed upserts, and
-- INSERT ... WHERE NOT EXISTS for the NULL-company template rows (a
-- UNIQUE constraint cannot dedupe NULL company ids).
-- ============================================================================

PRAGMA foreign_keys = ON;

-- ---- Permission groups ------------------------------------------------------
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('HR','الموارد البشرية','Human Resources',22);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('QUALITY','الجودة','Quality',23);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('ASSETS','الأصول الثابتة','Fixed Assets',24);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('PROJECTS','المشاريع','Projects',25);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('DOCUMENTS','المستندات','Documents',26);

UPDATE permissions SET group_code = 'PURCHASING' WHERE group_code = 'PURCHASES';

-- ---- Catalog reconciliation: the 14 missing PermCat codes -------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.CUSTOMER_LEDGER.VIEW','Customer Ledger - View','ACCOUNTING','CUSTOMER_LEDGER','VIEW','Customer Ledger - View','Customer Ledger - View','View the customer ledger (catalog reconciliation, migration 125)',544,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='CUSTOMER_LEDGER',operation_code='VIEW',name_ar='Customer Ledger - View',name_en='Customer Ledger - View',display_order=544,is_sensitive=0 WHERE permission_code='ACC.CUSTOMER_LEDGER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.FINANCIAL_ANALYSIS.VIEW','Financial Analysis - View','ACCOUNTING','FINANCIAL_ANALYSIS','VIEW','Financial Analysis - View','Financial Analysis - View','View financial analysis (catalog reconciliation, migration 125)',545,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='FINANCIAL_ANALYSIS',operation_code='VIEW',name_ar='Financial Analysis - View',name_en='Financial Analysis - View',display_order=545,is_sensitive=0 WHERE permission_code='ACC.FINANCIAL_ANALYSIS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.FINANCIAL_ANALYSIS.AI','Financial Analysis - AI','ACCOUNTING','FINANCIAL_ANALYSIS','AI','Financial Analysis - AI','Financial Analysis - AI','Run AI financial analysis (catalog reconciliation, migration 125)',546,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='FINANCIAL_ANALYSIS',operation_code='AI',name_ar='Financial Analysis - AI',name_en='Financial Analysis - AI',display_order=546,is_sensitive=0 WHERE permission_code='ACC.FINANCIAL_ANALYSIS.AI';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.FINANCIAL_STATEMENTS.VIEW','Financial Statements - View','ACCOUNTING','FINANCIAL_STATEMENTS','VIEW','Financial Statements - View','Financial Statements - View','View financial statements (catalog reconciliation, migration 125)',547,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='FINANCIAL_STATEMENTS',operation_code='VIEW',name_ar='Financial Statements - View',name_en='Financial Statements - View',display_order=547,is_sensitive=0 WHERE permission_code='ACC.FINANCIAL_STATEMENTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AI.ASSISTANT','AI Assistant','AI','ASSISTANT','VIEW','AI Assistant','AI Assistant','Use the AI assistant (catalog reconciliation, migration 125)',548,0);
UPDATE permissions SET group_code='AI',feature_code='ASSISTANT',operation_code='VIEW',name_ar='AI Assistant',name_en='AI Assistant',display_order=548,is_sensitive=0 WHERE permission_code='AI.ASSISTANT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AI.FINANCIAL_ANALYSIS','AI Financial Analysis','AI','FINANCIAL_ANALYSIS','VIEW','AI Financial Analysis','AI Financial Analysis','Use AI financial analysis (catalog reconciliation, migration 125)',549,0);
UPDATE permissions SET group_code='AI',feature_code='FINANCIAL_ANALYSIS',operation_code='VIEW',name_ar='AI Financial Analysis',name_en='AI Financial Analysis',display_order=549,is_sensitive=0 WHERE permission_code='AI.FINANCIAL_ANALYSIS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AI.INVENTORY_ANALYSIS','AI Inventory Analysis','AI','INVENTORY_ANALYSIS','VIEW','AI Inventory Analysis','AI Inventory Analysis','Use AI inventory analysis (catalog reconciliation, migration 125)',550,0);
UPDATE permissions SET group_code='AI',feature_code='INVENTORY_ANALYSIS',operation_code='VIEW',name_ar='AI Inventory Analysis',name_en='AI Inventory Analysis',display_order=550,is_sensitive=0 WHERE permission_code='AI.INVENTORY_ANALYSIS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AI.SALES_ANALYSIS','AI Sales Analysis','AI','SALES_ANALYSIS','VIEW','AI Sales Analysis','AI Sales Analysis','Use AI sales analysis (catalog reconciliation, migration 125)',551,0);
UPDATE permissions SET group_code='AI',feature_code='SALES_ANALYSIS',operation_code='VIEW',name_ar='AI Sales Analysis',name_en='AI Sales Analysis',display_order=551,is_sensitive=0 WHERE permission_code='AI.SALES_ANALYSIS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AI.FORECAST','AI Forecast','AI','FORECAST','VIEW','AI Forecast','AI Forecast','Use AI forecasting (catalog reconciliation, migration 125)',552,0);
UPDATE permissions SET group_code='AI',feature_code='FORECAST',operation_code='VIEW',name_ar='AI Forecast',name_en='AI Forecast',display_order=552,is_sensitive=0 WHERE permission_code='AI.FORECAST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AUDIT.SECURITY_EVENTS.VIEW','Security Events - View','AUDIT','SECURITY_EVENTS','VIEW','Security Events - View','Security Events - View','View security audit events (catalog reconciliation, migration 125)',553,0);
UPDATE permissions SET group_code='AUDIT',feature_code='SECURITY_EVENTS',operation_code='VIEW',name_ar='Security Events - View',name_en='Security Events - View',display_order=553,is_sensitive=0 WHERE permission_code='AUDIT.SECURITY_EVENTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.RECONCILIATION.VIEW','Bank Reconciliation - View','BANKING','RECONCILIATION','VIEW','Bank Reconciliation - View','Bank Reconciliation - View','View bank reconciliations (catalog reconciliation, migration 125)',554,0);
UPDATE permissions SET group_code='BANKING',feature_code='RECONCILIATION',operation_code='VIEW',name_ar='Bank Reconciliation - View',name_en='Bank Reconciliation - View',display_order=554,is_sensitive=0 WHERE permission_code='BANK.RECONCILIATION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.RECONCILIATION.EXECUTE','Bank Reconciliation - Execute','BANKING','RECONCILIATION','EXECUTE','Bank Reconciliation - Execute','Bank Reconciliation - Execute','Execute bank reconciliations (catalog reconciliation, migration 125)',555,0);
UPDATE permissions SET group_code='BANKING',feature_code='RECONCILIATION',operation_code='EXECUTE',name_ar='Bank Reconciliation - Execute',name_en='Bank Reconciliation - Execute',display_order=555,is_sensitive=0 WHERE permission_code='BANK.RECONCILIATION.EXECUTE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.PAYMENT_METHOD.MANAGE','Payment Method - Manage','VOUCHERS','PAYMENT_METHOD','MANAGE','Payment Method - Manage','Payment Method - Manage','Manage payment methods (catalog reconciliation, migration 125)',556,1);
UPDATE permissions SET group_code='VOUCHERS',feature_code='PAYMENT_METHOD',operation_code='MANAGE',name_ar='Payment Method - Manage',name_en='Payment Method - Manage',display_order=556,is_sensitive=1 WHERE permission_code='VCH.PAYMENT_METHOD.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.PERMISSION_TEMPLATES.MANAGE','Permission Templates - Manage','SYSTEM','PERMISSION_TEMPLATES','MANAGE','Permission Templates - Manage','Permission Templates - Manage','Manage permission templates and their assignments (migration 125)',557,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='PERMISSION_TEMPLATES',operation_code='MANAGE',name_ar='Permission Templates - Manage',name_en='Permission Templates - Manage',display_order=557,is_sensitive=1 WHERE permission_code='SYS.PERMISSION_TEMPLATES.MANAGE';

-- Role baselines for the reconciled codes (administrator: all; user: all but
-- the protected template-management key; viewer: VIEW operations).
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.CUSTOMER_LEDGER.VIEW','ACC.FINANCIAL_ANALYSIS.VIEW','ACC.FINANCIAL_ANALYSIS.AI','ACC.FINANCIAL_STATEMENTS.VIEW','AI.ASSISTANT','AI.FINANCIAL_ANALYSIS','AI.INVENTORY_ANALYSIS','AI.SALES_ANALYSIS','AI.FORECAST','AUDIT.SECURITY_EVENTS.VIEW','BANK.RECONCILIATION.VIEW','BANK.RECONCILIATION.EXECUTE','VCH.PAYMENT_METHOD.MANAGE','SYS.PERMISSION_TEMPLATES.MANAGE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.CUSTOMER_LEDGER.VIEW','ACC.FINANCIAL_ANALYSIS.VIEW','ACC.FINANCIAL_ANALYSIS.AI','ACC.FINANCIAL_STATEMENTS.VIEW','AI.ASSISTANT','AI.FINANCIAL_ANALYSIS','AI.INVENTORY_ANALYSIS','AI.SALES_ANALYSIS','AI.FORECAST','AUDIT.SECURITY_EVENTS.VIEW','BANK.RECONCILIATION.VIEW','BANK.RECONCILIATION.EXECUTE','VCH.PAYMENT_METHOD.MANAGE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.CUSTOMER_LEDGER.VIEW','ACC.FINANCIAL_ANALYSIS.VIEW','ACC.FINANCIAL_STATEMENTS.VIEW','AI.ASSISTANT','AI.FINANCIAL_ANALYSIS','AI.INVENTORY_ANALYSIS','AI.SALES_ANALYSIS','AI.FORECAST','AUDIT.SECURITY_EVENTS.VIEW','BANK.RECONCILIATION.VIEW');

-- ---- DOC.<KIND>.<OP> per-document-type family (104 keys) ---------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.VIEW','Sales Invoice - View','DOCUMENTS','SAL','VIEW','Sales Invoice - View','Sales Invoice - View','Per-document-type permission (migration 125): view on sales invoice documents.',560,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='VIEW',name_ar='Sales Invoice - View',name_en='Sales Invoice - View',display_order=560,is_sensitive=0 WHERE permission_code='DOC.SAL.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.CREATE','Sales Invoice - Create','DOCUMENTS','SAL','CREATE','Sales Invoice - Create','Sales Invoice - Create','Per-document-type permission (migration 125): create on sales invoice documents.',561,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='CREATE',name_ar='Sales Invoice - Create',name_en='Sales Invoice - Create',display_order=561,is_sensitive=0 WHERE permission_code='DOC.SAL.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.UPDATE','Sales Invoice - Update','DOCUMENTS','SAL','UPDATE','Sales Invoice - Update','Sales Invoice - Update','Per-document-type permission (migration 125): update on sales invoice documents.',562,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='UPDATE',name_ar='Sales Invoice - Update',name_en='Sales Invoice - Update',display_order=562,is_sensitive=0 WHERE permission_code='DOC.SAL.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.DELETE','Sales Invoice - Delete','DOCUMENTS','SAL','DELETE','Sales Invoice - Delete','Sales Invoice - Delete','Per-document-type permission (migration 125): delete on sales invoice documents.',563,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='DELETE',name_ar='Sales Invoice - Delete',name_en='Sales Invoice - Delete',display_order=563,is_sensitive=1 WHERE permission_code='DOC.SAL.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.PRINT','Sales Invoice - Print','DOCUMENTS','SAL','PRINT','Sales Invoice - Print','Sales Invoice - Print','Per-document-type permission (migration 125): print on sales invoice documents.',564,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='PRINT',name_ar='Sales Invoice - Print',name_en='Sales Invoice - Print',display_order=564,is_sensitive=0 WHERE permission_code='DOC.SAL.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.POST','Sales Invoice - Post','DOCUMENTS','SAL','POST','Sales Invoice - Post','Sales Invoice - Post','Per-document-type permission (migration 125): post on sales invoice documents.',565,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='POST',name_ar='Sales Invoice - Post',name_en='Sales Invoice - Post',display_order=565,is_sensitive=1 WHERE permission_code='DOC.SAL.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.FIELDS','Sales Invoice - Show Fields','DOCUMENTS','SAL','FIELDS','Sales Invoice - Show Fields','Sales Invoice - Show Fields','Per-document-type permission (migration 125): show fields on sales invoice documents.',566,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='FIELDS',name_ar='Sales Invoice - Show Fields',name_en='Sales Invoice - Show Fields',display_order=566,is_sensitive=1 WHERE permission_code='DOC.SAL.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SAL.ACCOUNT','Sales Invoice - Set Account','DOCUMENTS','SAL','ACCOUNT','Sales Invoice - Set Account','Sales Invoice - Set Account','Per-document-type permission (migration 125): set account on sales invoice documents.',567,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SAL',operation_code='ACCOUNT',name_ar='Sales Invoice - Set Account',name_en='Sales Invoice - Set Account',display_order=567,is_sensitive=1 WHERE permission_code='DOC.SAL.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.VIEW','Sales Return - View','DOCUMENTS','SRT','VIEW','Sales Return - View','Sales Return - View','Per-document-type permission (migration 125): view on sales return documents.',568,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='VIEW',name_ar='Sales Return - View',name_en='Sales Return - View',display_order=568,is_sensitive=0 WHERE permission_code='DOC.SRT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.CREATE','Sales Return - Create','DOCUMENTS','SRT','CREATE','Sales Return - Create','Sales Return - Create','Per-document-type permission (migration 125): create on sales return documents.',569,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='CREATE',name_ar='Sales Return - Create',name_en='Sales Return - Create',display_order=569,is_sensitive=0 WHERE permission_code='DOC.SRT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.UPDATE','Sales Return - Update','DOCUMENTS','SRT','UPDATE','Sales Return - Update','Sales Return - Update','Per-document-type permission (migration 125): update on sales return documents.',570,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='UPDATE',name_ar='Sales Return - Update',name_en='Sales Return - Update',display_order=570,is_sensitive=0 WHERE permission_code='DOC.SRT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.DELETE','Sales Return - Delete','DOCUMENTS','SRT','DELETE','Sales Return - Delete','Sales Return - Delete','Per-document-type permission (migration 125): delete on sales return documents.',571,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='DELETE',name_ar='Sales Return - Delete',name_en='Sales Return - Delete',display_order=571,is_sensitive=1 WHERE permission_code='DOC.SRT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.PRINT','Sales Return - Print','DOCUMENTS','SRT','PRINT','Sales Return - Print','Sales Return - Print','Per-document-type permission (migration 125): print on sales return documents.',572,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='PRINT',name_ar='Sales Return - Print',name_en='Sales Return - Print',display_order=572,is_sensitive=0 WHERE permission_code='DOC.SRT.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.POST','Sales Return - Post','DOCUMENTS','SRT','POST','Sales Return - Post','Sales Return - Post','Per-document-type permission (migration 125): post on sales return documents.',573,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='POST',name_ar='Sales Return - Post',name_en='Sales Return - Post',display_order=573,is_sensitive=1 WHERE permission_code='DOC.SRT.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.FIELDS','Sales Return - Show Fields','DOCUMENTS','SRT','FIELDS','Sales Return - Show Fields','Sales Return - Show Fields','Per-document-type permission (migration 125): show fields on sales return documents.',574,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='FIELDS',name_ar='Sales Return - Show Fields',name_en='Sales Return - Show Fields',display_order=574,is_sensitive=1 WHERE permission_code='DOC.SRT.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SRT.ACCOUNT','Sales Return - Set Account','DOCUMENTS','SRT','ACCOUNT','Sales Return - Set Account','Sales Return - Set Account','Per-document-type permission (migration 125): set account on sales return documents.',575,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SRT',operation_code='ACCOUNT',name_ar='Sales Return - Set Account',name_en='Sales Return - Set Account',display_order=575,is_sensitive=1 WHERE permission_code='DOC.SRT.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.VIEW','POS Sale - View','DOCUMENTS','POS','VIEW','POS Sale - View','POS Sale - View','Per-document-type permission (migration 125): view on pos sale documents.',576,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='VIEW',name_ar='POS Sale - View',name_en='POS Sale - View',display_order=576,is_sensitive=0 WHERE permission_code='DOC.POS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.CREATE','POS Sale - Create','DOCUMENTS','POS','CREATE','POS Sale - Create','POS Sale - Create','Per-document-type permission (migration 125): create on pos sale documents.',577,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='CREATE',name_ar='POS Sale - Create',name_en='POS Sale - Create',display_order=577,is_sensitive=0 WHERE permission_code='DOC.POS.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.UPDATE','POS Sale - Update','DOCUMENTS','POS','UPDATE','POS Sale - Update','POS Sale - Update','Per-document-type permission (migration 125): update on pos sale documents.',578,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='UPDATE',name_ar='POS Sale - Update',name_en='POS Sale - Update',display_order=578,is_sensitive=0 WHERE permission_code='DOC.POS.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.DELETE','POS Sale - Delete','DOCUMENTS','POS','DELETE','POS Sale - Delete','POS Sale - Delete','Per-document-type permission (migration 125): delete on pos sale documents.',579,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='DELETE',name_ar='POS Sale - Delete',name_en='POS Sale - Delete',display_order=579,is_sensitive=1 WHERE permission_code='DOC.POS.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.PRINT','POS Sale - Print','DOCUMENTS','POS','PRINT','POS Sale - Print','POS Sale - Print','Per-document-type permission (migration 125): print on pos sale documents.',580,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='PRINT',name_ar='POS Sale - Print',name_en='POS Sale - Print',display_order=580,is_sensitive=0 WHERE permission_code='DOC.POS.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.POST','POS Sale - Post','DOCUMENTS','POS','POST','POS Sale - Post','POS Sale - Post','Per-document-type permission (migration 125): post on pos sale documents.',581,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='POST',name_ar='POS Sale - Post',name_en='POS Sale - Post',display_order=581,is_sensitive=1 WHERE permission_code='DOC.POS.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.FIELDS','POS Sale - Show Fields','DOCUMENTS','POS','FIELDS','POS Sale - Show Fields','POS Sale - Show Fields','Per-document-type permission (migration 125): show fields on pos sale documents.',582,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='FIELDS',name_ar='POS Sale - Show Fields',name_en='POS Sale - Show Fields',display_order=582,is_sensitive=1 WHERE permission_code='DOC.POS.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.POS.ACCOUNT','POS Sale - Set Account','DOCUMENTS','POS','ACCOUNT','POS Sale - Set Account','POS Sale - Set Account','Per-document-type permission (migration 125): set account on pos sale documents.',583,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='POS',operation_code='ACCOUNT',name_ar='POS Sale - Set Account',name_en='POS Sale - Set Account',display_order=583,is_sensitive=1 WHERE permission_code='DOC.POS.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.VIEW','Purchase Invoice - View','DOCUMENTS','PUR','VIEW','Purchase Invoice - View','Purchase Invoice - View','Per-document-type permission (migration 125): view on purchase invoice documents.',584,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='VIEW',name_ar='Purchase Invoice - View',name_en='Purchase Invoice - View',display_order=584,is_sensitive=0 WHERE permission_code='DOC.PUR.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.CREATE','Purchase Invoice - Create','DOCUMENTS','PUR','CREATE','Purchase Invoice - Create','Purchase Invoice - Create','Per-document-type permission (migration 125): create on purchase invoice documents.',585,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='CREATE',name_ar='Purchase Invoice - Create',name_en='Purchase Invoice - Create',display_order=585,is_sensitive=0 WHERE permission_code='DOC.PUR.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.UPDATE','Purchase Invoice - Update','DOCUMENTS','PUR','UPDATE','Purchase Invoice - Update','Purchase Invoice - Update','Per-document-type permission (migration 125): update on purchase invoice documents.',586,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='UPDATE',name_ar='Purchase Invoice - Update',name_en='Purchase Invoice - Update',display_order=586,is_sensitive=0 WHERE permission_code='DOC.PUR.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.DELETE','Purchase Invoice - Delete','DOCUMENTS','PUR','DELETE','Purchase Invoice - Delete','Purchase Invoice - Delete','Per-document-type permission (migration 125): delete on purchase invoice documents.',587,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='DELETE',name_ar='Purchase Invoice - Delete',name_en='Purchase Invoice - Delete',display_order=587,is_sensitive=1 WHERE permission_code='DOC.PUR.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.PRINT','Purchase Invoice - Print','DOCUMENTS','PUR','PRINT','Purchase Invoice - Print','Purchase Invoice - Print','Per-document-type permission (migration 125): print on purchase invoice documents.',588,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='PRINT',name_ar='Purchase Invoice - Print',name_en='Purchase Invoice - Print',display_order=588,is_sensitive=0 WHERE permission_code='DOC.PUR.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.POST','Purchase Invoice - Post','DOCUMENTS','PUR','POST','Purchase Invoice - Post','Purchase Invoice - Post','Per-document-type permission (migration 125): post on purchase invoice documents.',589,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='POST',name_ar='Purchase Invoice - Post',name_en='Purchase Invoice - Post',display_order=589,is_sensitive=1 WHERE permission_code='DOC.PUR.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.FIELDS','Purchase Invoice - Show Fields','DOCUMENTS','PUR','FIELDS','Purchase Invoice - Show Fields','Purchase Invoice - Show Fields','Per-document-type permission (migration 125): show fields on purchase invoice documents.',590,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='FIELDS',name_ar='Purchase Invoice - Show Fields',name_en='Purchase Invoice - Show Fields',display_order=590,is_sensitive=1 WHERE permission_code='DOC.PUR.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PUR.ACCOUNT','Purchase Invoice - Set Account','DOCUMENTS','PUR','ACCOUNT','Purchase Invoice - Set Account','Purchase Invoice - Set Account','Per-document-type permission (migration 125): set account on purchase invoice documents.',591,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PUR',operation_code='ACCOUNT',name_ar='Purchase Invoice - Set Account',name_en='Purchase Invoice - Set Account',display_order=591,is_sensitive=1 WHERE permission_code='DOC.PUR.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.VIEW','Purchase Return - View','DOCUMENTS','PRT','VIEW','Purchase Return - View','Purchase Return - View','Per-document-type permission (migration 125): view on purchase return documents.',592,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='VIEW',name_ar='Purchase Return - View',name_en='Purchase Return - View',display_order=592,is_sensitive=0 WHERE permission_code='DOC.PRT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.CREATE','Purchase Return - Create','DOCUMENTS','PRT','CREATE','Purchase Return - Create','Purchase Return - Create','Per-document-type permission (migration 125): create on purchase return documents.',593,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='CREATE',name_ar='Purchase Return - Create',name_en='Purchase Return - Create',display_order=593,is_sensitive=0 WHERE permission_code='DOC.PRT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.UPDATE','Purchase Return - Update','DOCUMENTS','PRT','UPDATE','Purchase Return - Update','Purchase Return - Update','Per-document-type permission (migration 125): update on purchase return documents.',594,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='UPDATE',name_ar='Purchase Return - Update',name_en='Purchase Return - Update',display_order=594,is_sensitive=0 WHERE permission_code='DOC.PRT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.DELETE','Purchase Return - Delete','DOCUMENTS','PRT','DELETE','Purchase Return - Delete','Purchase Return - Delete','Per-document-type permission (migration 125): delete on purchase return documents.',595,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='DELETE',name_ar='Purchase Return - Delete',name_en='Purchase Return - Delete',display_order=595,is_sensitive=1 WHERE permission_code='DOC.PRT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.PRINT','Purchase Return - Print','DOCUMENTS','PRT','PRINT','Purchase Return - Print','Purchase Return - Print','Per-document-type permission (migration 125): print on purchase return documents.',596,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='PRINT',name_ar='Purchase Return - Print',name_en='Purchase Return - Print',display_order=596,is_sensitive=0 WHERE permission_code='DOC.PRT.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.POST','Purchase Return - Post','DOCUMENTS','PRT','POST','Purchase Return - Post','Purchase Return - Post','Per-document-type permission (migration 125): post on purchase return documents.',597,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='POST',name_ar='Purchase Return - Post',name_en='Purchase Return - Post',display_order=597,is_sensitive=1 WHERE permission_code='DOC.PRT.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.FIELDS','Purchase Return - Show Fields','DOCUMENTS','PRT','FIELDS','Purchase Return - Show Fields','Purchase Return - Show Fields','Per-document-type permission (migration 125): show fields on purchase return documents.',598,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='FIELDS',name_ar='Purchase Return - Show Fields',name_en='Purchase Return - Show Fields',display_order=598,is_sensitive=1 WHERE permission_code='DOC.PRT.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PRT.ACCOUNT','Purchase Return - Set Account','DOCUMENTS','PRT','ACCOUNT','Purchase Return - Set Account','Purchase Return - Set Account','Per-document-type permission (migration 125): set account on purchase return documents.',599,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PRT',operation_code='ACCOUNT',name_ar='Purchase Return - Set Account',name_en='Purchase Return - Set Account',display_order=599,is_sensitive=1 WHERE permission_code='DOC.PRT.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.VIEW','Stock Entry - View','DOCUMENTS','ENT','VIEW','Stock Entry - View','Stock Entry - View','Per-document-type permission (migration 125): view on stock entry documents.',600,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='VIEW',name_ar='Stock Entry - View',name_en='Stock Entry - View',display_order=600,is_sensitive=0 WHERE permission_code='DOC.ENT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.CREATE','Stock Entry - Create','DOCUMENTS','ENT','CREATE','Stock Entry - Create','Stock Entry - Create','Per-document-type permission (migration 125): create on stock entry documents.',601,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='CREATE',name_ar='Stock Entry - Create',name_en='Stock Entry - Create',display_order=601,is_sensitive=0 WHERE permission_code='DOC.ENT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.UPDATE','Stock Entry - Update','DOCUMENTS','ENT','UPDATE','Stock Entry - Update','Stock Entry - Update','Per-document-type permission (migration 125): update on stock entry documents.',602,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='UPDATE',name_ar='Stock Entry - Update',name_en='Stock Entry - Update',display_order=602,is_sensitive=0 WHERE permission_code='DOC.ENT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.DELETE','Stock Entry - Delete','DOCUMENTS','ENT','DELETE','Stock Entry - Delete','Stock Entry - Delete','Per-document-type permission (migration 125): delete on stock entry documents.',603,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='DELETE',name_ar='Stock Entry - Delete',name_en='Stock Entry - Delete',display_order=603,is_sensitive=1 WHERE permission_code='DOC.ENT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.PRINT','Stock Entry - Print','DOCUMENTS','ENT','PRINT','Stock Entry - Print','Stock Entry - Print','Per-document-type permission (migration 125): print on stock entry documents.',604,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='PRINT',name_ar='Stock Entry - Print',name_en='Stock Entry - Print',display_order=604,is_sensitive=0 WHERE permission_code='DOC.ENT.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.POST','Stock Entry - Post','DOCUMENTS','ENT','POST','Stock Entry - Post','Stock Entry - Post','Per-document-type permission (migration 125): post on stock entry documents.',605,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='POST',name_ar='Stock Entry - Post',name_en='Stock Entry - Post',display_order=605,is_sensitive=1 WHERE permission_code='DOC.ENT.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.FIELDS','Stock Entry - Show Fields','DOCUMENTS','ENT','FIELDS','Stock Entry - Show Fields','Stock Entry - Show Fields','Per-document-type permission (migration 125): show fields on stock entry documents.',606,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='FIELDS',name_ar='Stock Entry - Show Fields',name_en='Stock Entry - Show Fields',display_order=606,is_sensitive=1 WHERE permission_code='DOC.ENT.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.ENT.ACCOUNT','Stock Entry - Set Account','DOCUMENTS','ENT','ACCOUNT','Stock Entry - Set Account','Stock Entry - Set Account','Per-document-type permission (migration 125): set account on stock entry documents.',607,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='ENT',operation_code='ACCOUNT',name_ar='Stock Entry - Set Account',name_en='Stock Entry - Set Account',display_order=607,is_sensitive=1 WHERE permission_code='DOC.ENT.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.VIEW','Stock Exit - View','DOCUMENTS','EXT','VIEW','Stock Exit - View','Stock Exit - View','Per-document-type permission (migration 125): view on stock exit documents.',608,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='VIEW',name_ar='Stock Exit - View',name_en='Stock Exit - View',display_order=608,is_sensitive=0 WHERE permission_code='DOC.EXT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.CREATE','Stock Exit - Create','DOCUMENTS','EXT','CREATE','Stock Exit - Create','Stock Exit - Create','Per-document-type permission (migration 125): create on stock exit documents.',609,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='CREATE',name_ar='Stock Exit - Create',name_en='Stock Exit - Create',display_order=609,is_sensitive=0 WHERE permission_code='DOC.EXT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.UPDATE','Stock Exit - Update','DOCUMENTS','EXT','UPDATE','Stock Exit - Update','Stock Exit - Update','Per-document-type permission (migration 125): update on stock exit documents.',610,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='UPDATE',name_ar='Stock Exit - Update',name_en='Stock Exit - Update',display_order=610,is_sensitive=0 WHERE permission_code='DOC.EXT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.DELETE','Stock Exit - Delete','DOCUMENTS','EXT','DELETE','Stock Exit - Delete','Stock Exit - Delete','Per-document-type permission (migration 125): delete on stock exit documents.',611,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='DELETE',name_ar='Stock Exit - Delete',name_en='Stock Exit - Delete',display_order=611,is_sensitive=1 WHERE permission_code='DOC.EXT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.PRINT','Stock Exit - Print','DOCUMENTS','EXT','PRINT','Stock Exit - Print','Stock Exit - Print','Per-document-type permission (migration 125): print on stock exit documents.',612,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='PRINT',name_ar='Stock Exit - Print',name_en='Stock Exit - Print',display_order=612,is_sensitive=0 WHERE permission_code='DOC.EXT.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.POST','Stock Exit - Post','DOCUMENTS','EXT','POST','Stock Exit - Post','Stock Exit - Post','Per-document-type permission (migration 125): post on stock exit documents.',613,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='POST',name_ar='Stock Exit - Post',name_en='Stock Exit - Post',display_order=613,is_sensitive=1 WHERE permission_code='DOC.EXT.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.FIELDS','Stock Exit - Show Fields','DOCUMENTS','EXT','FIELDS','Stock Exit - Show Fields','Stock Exit - Show Fields','Per-document-type permission (migration 125): show fields on stock exit documents.',614,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='FIELDS',name_ar='Stock Exit - Show Fields',name_en='Stock Exit - Show Fields',display_order=614,is_sensitive=1 WHERE permission_code='DOC.EXT.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.EXT.ACCOUNT','Stock Exit - Set Account','DOCUMENTS','EXT','ACCOUNT','Stock Exit - Set Account','Stock Exit - Set Account','Per-document-type permission (migration 125): set account on stock exit documents.',615,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='EXT',operation_code='ACCOUNT',name_ar='Stock Exit - Set Account',name_en='Stock Exit - Set Account',display_order=615,is_sensitive=1 WHERE permission_code='DOC.EXT.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.VIEW','Stock Transfer - View','DOCUMENTS','TRN','VIEW','Stock Transfer - View','Stock Transfer - View','Per-document-type permission (migration 125): view on stock transfer documents.',616,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='VIEW',name_ar='Stock Transfer - View',name_en='Stock Transfer - View',display_order=616,is_sensitive=0 WHERE permission_code='DOC.TRN.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.CREATE','Stock Transfer - Create','DOCUMENTS','TRN','CREATE','Stock Transfer - Create','Stock Transfer - Create','Per-document-type permission (migration 125): create on stock transfer documents.',617,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='CREATE',name_ar='Stock Transfer - Create',name_en='Stock Transfer - Create',display_order=617,is_sensitive=0 WHERE permission_code='DOC.TRN.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.UPDATE','Stock Transfer - Update','DOCUMENTS','TRN','UPDATE','Stock Transfer - Update','Stock Transfer - Update','Per-document-type permission (migration 125): update on stock transfer documents.',618,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='UPDATE',name_ar='Stock Transfer - Update',name_en='Stock Transfer - Update',display_order=618,is_sensitive=0 WHERE permission_code='DOC.TRN.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.DELETE','Stock Transfer - Delete','DOCUMENTS','TRN','DELETE','Stock Transfer - Delete','Stock Transfer - Delete','Per-document-type permission (migration 125): delete on stock transfer documents.',619,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='DELETE',name_ar='Stock Transfer - Delete',name_en='Stock Transfer - Delete',display_order=619,is_sensitive=1 WHERE permission_code='DOC.TRN.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.PRINT','Stock Transfer - Print','DOCUMENTS','TRN','PRINT','Stock Transfer - Print','Stock Transfer - Print','Per-document-type permission (migration 125): print on stock transfer documents.',620,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='PRINT',name_ar='Stock Transfer - Print',name_en='Stock Transfer - Print',display_order=620,is_sensitive=0 WHERE permission_code='DOC.TRN.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.POST','Stock Transfer - Post','DOCUMENTS','TRN','POST','Stock Transfer - Post','Stock Transfer - Post','Per-document-type permission (migration 125): post on stock transfer documents.',621,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='POST',name_ar='Stock Transfer - Post',name_en='Stock Transfer - Post',display_order=621,is_sensitive=1 WHERE permission_code='DOC.TRN.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.FIELDS','Stock Transfer - Show Fields','DOCUMENTS','TRN','FIELDS','Stock Transfer - Show Fields','Stock Transfer - Show Fields','Per-document-type permission (migration 125): show fields on stock transfer documents.',622,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='FIELDS',name_ar='Stock Transfer - Show Fields',name_en='Stock Transfer - Show Fields',display_order=622,is_sensitive=1 WHERE permission_code='DOC.TRN.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.TRN.ACCOUNT','Stock Transfer - Set Account','DOCUMENTS','TRN','ACCOUNT','Stock Transfer - Set Account','Stock Transfer - Set Account','Per-document-type permission (migration 125): set account on stock transfer documents.',623,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='TRN',operation_code='ACCOUNT',name_ar='Stock Transfer - Set Account',name_en='Stock Transfer - Set Account',display_order=623,is_sensitive=1 WHERE permission_code='DOC.TRN.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.VIEW','Opening Stock - View','DOCUMENTS','OPN','VIEW','Opening Stock - View','Opening Stock - View','Per-document-type permission (migration 125): view on opening stock documents.',624,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='VIEW',name_ar='Opening Stock - View',name_en='Opening Stock - View',display_order=624,is_sensitive=0 WHERE permission_code='DOC.OPN.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.CREATE','Opening Stock - Create','DOCUMENTS','OPN','CREATE','Opening Stock - Create','Opening Stock - Create','Per-document-type permission (migration 125): create on opening stock documents.',625,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='CREATE',name_ar='Opening Stock - Create',name_en='Opening Stock - Create',display_order=625,is_sensitive=0 WHERE permission_code='DOC.OPN.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.UPDATE','Opening Stock - Update','DOCUMENTS','OPN','UPDATE','Opening Stock - Update','Opening Stock - Update','Per-document-type permission (migration 125): update on opening stock documents.',626,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='UPDATE',name_ar='Opening Stock - Update',name_en='Opening Stock - Update',display_order=626,is_sensitive=0 WHERE permission_code='DOC.OPN.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.DELETE','Opening Stock - Delete','DOCUMENTS','OPN','DELETE','Opening Stock - Delete','Opening Stock - Delete','Per-document-type permission (migration 125): delete on opening stock documents.',627,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='DELETE',name_ar='Opening Stock - Delete',name_en='Opening Stock - Delete',display_order=627,is_sensitive=1 WHERE permission_code='DOC.OPN.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.PRINT','Opening Stock - Print','DOCUMENTS','OPN','PRINT','Opening Stock - Print','Opening Stock - Print','Per-document-type permission (migration 125): print on opening stock documents.',628,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='PRINT',name_ar='Opening Stock - Print',name_en='Opening Stock - Print',display_order=628,is_sensitive=0 WHERE permission_code='DOC.OPN.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.POST','Opening Stock - Post','DOCUMENTS','OPN','POST','Opening Stock - Post','Opening Stock - Post','Per-document-type permission (migration 125): post on opening stock documents.',629,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='POST',name_ar='Opening Stock - Post',name_en='Opening Stock - Post',display_order=629,is_sensitive=1 WHERE permission_code='DOC.OPN.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.FIELDS','Opening Stock - Show Fields','DOCUMENTS','OPN','FIELDS','Opening Stock - Show Fields','Opening Stock - Show Fields','Per-document-type permission (migration 125): show fields on opening stock documents.',630,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='FIELDS',name_ar='Opening Stock - Show Fields',name_en='Opening Stock - Show Fields',display_order=630,is_sensitive=1 WHERE permission_code='DOC.OPN.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.OPN.ACCOUNT','Opening Stock - Set Account','DOCUMENTS','OPN','ACCOUNT','Opening Stock - Set Account','Opening Stock - Set Account','Per-document-type permission (migration 125): set account on opening stock documents.',631,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='OPN',operation_code='ACCOUNT',name_ar='Opening Stock - Set Account',name_en='Opening Stock - Set Account',display_order=631,is_sensitive=1 WHERE permission_code='DOC.OPN.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.VIEW','Inventory Count - View','DOCUMENTS','CNT','VIEW','Inventory Count - View','Inventory Count - View','Per-document-type permission (migration 125): view on inventory count documents.',632,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='VIEW',name_ar='Inventory Count - View',name_en='Inventory Count - View',display_order=632,is_sensitive=0 WHERE permission_code='DOC.CNT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.CREATE','Inventory Count - Create','DOCUMENTS','CNT','CREATE','Inventory Count - Create','Inventory Count - Create','Per-document-type permission (migration 125): create on inventory count documents.',633,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='CREATE',name_ar='Inventory Count - Create',name_en='Inventory Count - Create',display_order=633,is_sensitive=0 WHERE permission_code='DOC.CNT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.UPDATE','Inventory Count - Update','DOCUMENTS','CNT','UPDATE','Inventory Count - Update','Inventory Count - Update','Per-document-type permission (migration 125): update on inventory count documents.',634,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='UPDATE',name_ar='Inventory Count - Update',name_en='Inventory Count - Update',display_order=634,is_sensitive=0 WHERE permission_code='DOC.CNT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.DELETE','Inventory Count - Delete','DOCUMENTS','CNT','DELETE','Inventory Count - Delete','Inventory Count - Delete','Per-document-type permission (migration 125): delete on inventory count documents.',635,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='DELETE',name_ar='Inventory Count - Delete',name_en='Inventory Count - Delete',display_order=635,is_sensitive=1 WHERE permission_code='DOC.CNT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.PRINT','Inventory Count - Print','DOCUMENTS','CNT','PRINT','Inventory Count - Print','Inventory Count - Print','Per-document-type permission (migration 125): print on inventory count documents.',636,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='PRINT',name_ar='Inventory Count - Print',name_en='Inventory Count - Print',display_order=636,is_sensitive=0 WHERE permission_code='DOC.CNT.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.POST','Inventory Count - Post','DOCUMENTS','CNT','POST','Inventory Count - Post','Inventory Count - Post','Per-document-type permission (migration 125): post on inventory count documents.',637,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='POST',name_ar='Inventory Count - Post',name_en='Inventory Count - Post',display_order=637,is_sensitive=1 WHERE permission_code='DOC.CNT.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.FIELDS','Inventory Count - Show Fields','DOCUMENTS','CNT','FIELDS','Inventory Count - Show Fields','Inventory Count - Show Fields','Per-document-type permission (migration 125): show fields on inventory count documents.',638,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='FIELDS',name_ar='Inventory Count - Show Fields',name_en='Inventory Count - Show Fields',display_order=638,is_sensitive=1 WHERE permission_code='DOC.CNT.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.CNT.ACCOUNT','Inventory Count - Set Account','DOCUMENTS','CNT','ACCOUNT','Inventory Count - Set Account','Inventory Count - Set Account','Per-document-type permission (migration 125): set account on inventory count documents.',639,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='CNT',operation_code='ACCOUNT',name_ar='Inventory Count - Set Account',name_en='Inventory Count - Set Account',display_order=639,is_sensitive=1 WHERE permission_code='DOC.CNT.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.VIEW','Quotation - View','DOCUMENTS','QUO','VIEW','Quotation - View','Quotation - View','Per-document-type permission (migration 125): view on quotation documents.',640,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='VIEW',name_ar='Quotation - View',name_en='Quotation - View',display_order=640,is_sensitive=0 WHERE permission_code='DOC.QUO.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.CREATE','Quotation - Create','DOCUMENTS','QUO','CREATE','Quotation - Create','Quotation - Create','Per-document-type permission (migration 125): create on quotation documents.',641,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='CREATE',name_ar='Quotation - Create',name_en='Quotation - Create',display_order=641,is_sensitive=0 WHERE permission_code='DOC.QUO.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.UPDATE','Quotation - Update','DOCUMENTS','QUO','UPDATE','Quotation - Update','Quotation - Update','Per-document-type permission (migration 125): update on quotation documents.',642,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='UPDATE',name_ar='Quotation - Update',name_en='Quotation - Update',display_order=642,is_sensitive=0 WHERE permission_code='DOC.QUO.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.DELETE','Quotation - Delete','DOCUMENTS','QUO','DELETE','Quotation - Delete','Quotation - Delete','Per-document-type permission (migration 125): delete on quotation documents.',643,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='DELETE',name_ar='Quotation - Delete',name_en='Quotation - Delete',display_order=643,is_sensitive=1 WHERE permission_code='DOC.QUO.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.PRINT','Quotation - Print','DOCUMENTS','QUO','PRINT','Quotation - Print','Quotation - Print','Per-document-type permission (migration 125): print on quotation documents.',644,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='PRINT',name_ar='Quotation - Print',name_en='Quotation - Print',display_order=644,is_sensitive=0 WHERE permission_code='DOC.QUO.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.POST','Quotation - Post','DOCUMENTS','QUO','POST','Quotation - Post','Quotation - Post','Per-document-type permission (migration 125): post on quotation documents.',645,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='POST',name_ar='Quotation - Post',name_en='Quotation - Post',display_order=645,is_sensitive=1 WHERE permission_code='DOC.QUO.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.FIELDS','Quotation - Show Fields','DOCUMENTS','QUO','FIELDS','Quotation - Show Fields','Quotation - Show Fields','Per-document-type permission (migration 125): show fields on quotation documents.',646,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='FIELDS',name_ar='Quotation - Show Fields',name_en='Quotation - Show Fields',display_order=646,is_sensitive=1 WHERE permission_code='DOC.QUO.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.QUO.ACCOUNT','Quotation - Set Account','DOCUMENTS','QUO','ACCOUNT','Quotation - Set Account','Quotation - Set Account','Per-document-type permission (migration 125): set account on quotation documents.',647,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='QUO',operation_code='ACCOUNT',name_ar='Quotation - Set Account',name_en='Quotation - Set Account',display_order=647,is_sensitive=1 WHERE permission_code='DOC.QUO.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.VIEW','Sales Order - View','DOCUMENTS','SO','VIEW','Sales Order - View','Sales Order - View','Per-document-type permission (migration 125): view on sales order documents.',648,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='VIEW',name_ar='Sales Order - View',name_en='Sales Order - View',display_order=648,is_sensitive=0 WHERE permission_code='DOC.SO.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.CREATE','Sales Order - Create','DOCUMENTS','SO','CREATE','Sales Order - Create','Sales Order - Create','Per-document-type permission (migration 125): create on sales order documents.',649,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='CREATE',name_ar='Sales Order - Create',name_en='Sales Order - Create',display_order=649,is_sensitive=0 WHERE permission_code='DOC.SO.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.UPDATE','Sales Order - Update','DOCUMENTS','SO','UPDATE','Sales Order - Update','Sales Order - Update','Per-document-type permission (migration 125): update on sales order documents.',650,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='UPDATE',name_ar='Sales Order - Update',name_en='Sales Order - Update',display_order=650,is_sensitive=0 WHERE permission_code='DOC.SO.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.DELETE','Sales Order - Delete','DOCUMENTS','SO','DELETE','Sales Order - Delete','Sales Order - Delete','Per-document-type permission (migration 125): delete on sales order documents.',651,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='DELETE',name_ar='Sales Order - Delete',name_en='Sales Order - Delete',display_order=651,is_sensitive=1 WHERE permission_code='DOC.SO.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.PRINT','Sales Order - Print','DOCUMENTS','SO','PRINT','Sales Order - Print','Sales Order - Print','Per-document-type permission (migration 125): print on sales order documents.',652,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='PRINT',name_ar='Sales Order - Print',name_en='Sales Order - Print',display_order=652,is_sensitive=0 WHERE permission_code='DOC.SO.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.POST','Sales Order - Post','DOCUMENTS','SO','POST','Sales Order - Post','Sales Order - Post','Per-document-type permission (migration 125): post on sales order documents.',653,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='POST',name_ar='Sales Order - Post',name_en='Sales Order - Post',display_order=653,is_sensitive=1 WHERE permission_code='DOC.SO.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.FIELDS','Sales Order - Show Fields','DOCUMENTS','SO','FIELDS','Sales Order - Show Fields','Sales Order - Show Fields','Per-document-type permission (migration 125): show fields on sales order documents.',654,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='FIELDS',name_ar='Sales Order - Show Fields',name_en='Sales Order - Show Fields',display_order=654,is_sensitive=1 WHERE permission_code='DOC.SO.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.SO.ACCOUNT','Sales Order - Set Account','DOCUMENTS','SO','ACCOUNT','Sales Order - Set Account','Sales Order - Set Account','Per-document-type permission (migration 125): set account on sales order documents.',655,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='SO',operation_code='ACCOUNT',name_ar='Sales Order - Set Account',name_en='Sales Order - Set Account',display_order=655,is_sensitive=1 WHERE permission_code='DOC.SO.ACCOUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.VIEW','Purchase Order - View','DOCUMENTS','PO','VIEW','Purchase Order - View','Purchase Order - View','Per-document-type permission (migration 125): view on purchase order documents.',656,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='VIEW',name_ar='Purchase Order - View',name_en='Purchase Order - View',display_order=656,is_sensitive=0 WHERE permission_code='DOC.PO.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.CREATE','Purchase Order - Create','DOCUMENTS','PO','CREATE','Purchase Order - Create','Purchase Order - Create','Per-document-type permission (migration 125): create on purchase order documents.',657,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='CREATE',name_ar='Purchase Order - Create',name_en='Purchase Order - Create',display_order=657,is_sensitive=0 WHERE permission_code='DOC.PO.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.UPDATE','Purchase Order - Update','DOCUMENTS','PO','UPDATE','Purchase Order - Update','Purchase Order - Update','Per-document-type permission (migration 125): update on purchase order documents.',658,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='UPDATE',name_ar='Purchase Order - Update',name_en='Purchase Order - Update',display_order=658,is_sensitive=0 WHERE permission_code='DOC.PO.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.DELETE','Purchase Order - Delete','DOCUMENTS','PO','DELETE','Purchase Order - Delete','Purchase Order - Delete','Per-document-type permission (migration 125): delete on purchase order documents.',659,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='DELETE',name_ar='Purchase Order - Delete',name_en='Purchase Order - Delete',display_order=659,is_sensitive=1 WHERE permission_code='DOC.PO.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.PRINT','Purchase Order - Print','DOCUMENTS','PO','PRINT','Purchase Order - Print','Purchase Order - Print','Per-document-type permission (migration 125): print on purchase order documents.',660,0);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='PRINT',name_ar='Purchase Order - Print',name_en='Purchase Order - Print',display_order=660,is_sensitive=0 WHERE permission_code='DOC.PO.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.POST','Purchase Order - Post','DOCUMENTS','PO','POST','Purchase Order - Post','Purchase Order - Post','Per-document-type permission (migration 125): post on purchase order documents.',661,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='POST',name_ar='Purchase Order - Post',name_en='Purchase Order - Post',display_order=661,is_sensitive=1 WHERE permission_code='DOC.PO.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.FIELDS','Purchase Order - Show Fields','DOCUMENTS','PO','FIELDS','Purchase Order - Show Fields','Purchase Order - Show Fields','Per-document-type permission (migration 125): show fields on purchase order documents.',662,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='FIELDS',name_ar='Purchase Order - Show Fields',name_en='Purchase Order - Show Fields',display_order=662,is_sensitive=1 WHERE permission_code='DOC.PO.FIELDS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DOC.PO.ACCOUNT','Purchase Order - Set Account','DOCUMENTS','PO','ACCOUNT','Purchase Order - Set Account','Purchase Order - Set Account','Per-document-type permission (migration 125): set account on purchase order documents.',663,1);
UPDATE permissions SET group_code='DOCUMENTS',feature_code='PO',operation_code='ACCOUNT',name_ar='Purchase Order - Set Account',name_en='Purchase Order - Set Account',display_order=663,is_sensitive=1 WHERE permission_code='DOC.PO.ACCOUNT';

-- ---- DOC role-baseline mirroring --------------------------------------------
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.INVOICE.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SAL.VIEW','DOC.SAL.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.INVOICE.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SAL.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.INVOICE.UPDATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SAL.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.INVOICE.DELETE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SAL.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.INVOICE.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SAL.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.RETURN.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SRT.VIEW','DOC.SRT.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.RETURN.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SRT.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.RETURN.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SRT.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.QUOTATION.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.QUO.VIEW','DOC.QUO.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.QUOTATION.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.QUO.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.QUOTATION.UPDATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.QUO.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.QUOTATION.DELETE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.QUO.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.INVOICE.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PUR.VIEW','DOC.PUR.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.INVOICE.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PUR.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.INVOICE.UPDATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PUR.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.INVOICE.DELETE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PUR.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.INVOICE.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PUR.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.RETURN.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PRT.VIEW','DOC.PRT.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.RETURN.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PRT.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.RETURN.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PRT.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'POS.INVOICE.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.POS.CREATE','DOC.POS.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'POS.INVOICE.DELETE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.POS.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'POS.REPORTS.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.POS.VIEW','DOC.POS.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'INV.STOCK.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.ENT.VIEW','DOC.ENT.PRINT','DOC.EXT.VIEW','DOC.EXT.PRINT','DOC.TRN.VIEW','DOC.TRN.PRINT','DOC.OPN.VIEW','DOC.OPN.PRINT','DOC.CNT.VIEW','DOC.CNT.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'INV.STOCK.ADJUST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.ENT.CREATE','DOC.ENT.UPDATE','DOC.ENT.DELETE','DOC.EXT.CREATE','DOC.EXT.UPDATE','DOC.EXT.DELETE','DOC.OPN.CREATE','DOC.OPN.UPDATE','DOC.OPN.DELETE','DOC.CNT.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'INV.STOCK.TRANSFER'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.TRN.CREATE','DOC.TRN.UPDATE','DOC.TRN.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'INV.STOCK.COUNT'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.CNT.CREATE','DOC.CNT.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'INV.STOCK.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.ENT.POST','DOC.EXT.POST','DOC.TRN.POST','DOC.OPN.POST','DOC.CNT.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.ORDER.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SO.VIEW','DOC.SO.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.ORDER.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SO.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.ORDER.UPDATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SO.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.ORDER.CANCEL'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SO.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'SALES.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.SO.POST');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.ORDER.VIEW'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PO.VIEW','DOC.PO.PRINT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.ORDER.CREATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PO.CREATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.ORDER.UPDATE'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PO.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.ORDER.CANCEL'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PO.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT rp.role_id, dp.permission_id
FROM role_permissions rp
JOIN permissions gp ON gp.permission_id = rp.permission_id AND gp.permission_code = 'PURCH.POST'
CROSS JOIN permissions dp
WHERE dp.permission_code IN ('DOC.PO.POST');

-- ---- Template tables ----------------------------------------------------------
CREATE TABLE IF NOT EXISTS permission_templates (
    template_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NULL,
    template_code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT NOT NULL,
    description TEXT NULL,
    is_system INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by TEXT NULL,
    updated_at TEXT NULL,
    UNIQUE(company_id, template_code)
);
CREATE TABLE IF NOT EXISTS permission_template_grants (
    template_id INTEGER NOT NULL,
    permission_id INTEGER NOT NULL,
    PRIMARY KEY (template_id, permission_id),
    FOREIGN KEY(template_id) REFERENCES permission_templates(template_id),
    FOREIGN KEY(permission_id) REFERENCES permissions(permission_id)
);
CREATE TABLE IF NOT EXISTS user_permission_templates (
    user_id TEXT NOT NULL,
    template_id INTEGER NOT NULL,
    assigned_by TEXT NULL,
    assigned_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_id, template_id),
    FOREIGN KEY(user_id) REFERENCES users(user_id),
    FOREIGN KEY(template_id) REFERENCES permission_templates(template_id)
);
CREATE INDEX IF NOT EXISTS ix_user_permission_templates_template ON user_permission_templates(template_id);
CREATE INDEX IF NOT EXISTS ix_permission_templates_company ON permission_templates(company_id, template_code);

-- ---- Seeded global system templates ------------------------------------------
INSERT INTO permission_templates(company_id, template_code, name_en, name_ar, description, is_system, is_active, is_deleted, created_by, updated_by)
SELECT NULL, 'SYS_READ_ONLY', 'Read Only', 'Read Only', 'Every VIEW operation plus report printing. Seeded system template (migration 125); copy it to adapt.', 1, 1, 0, 'system', 'system'
WHERE NOT EXISTS (SELECT 1 FROM permission_templates WHERE company_id IS NULL AND template_code = 'SYS_READ_ONLY');
INSERT OR IGNORE INTO permission_template_grants (template_id, permission_id)
SELECT t.template_id, p.permission_id FROM permission_templates t CROSS JOIN permissions p
WHERE t.company_id IS NULL AND t.template_code = 'SYS_READ_ONLY' AND p.is_active = 1 AND (p.operation_code = 'VIEW' OR p.permission_code = 'REPORT.PRINT');
INSERT INTO permission_templates(company_id, template_code, name_en, name_ar, description, is_system, is_active, is_deleted, created_by, updated_by)
SELECT NULL, 'SYS_CASHIER', 'Cashier', 'Cashier', 'The POS family, POS document rights and cash-sale creation. Seeded system template (migration 125); copy it to adapt.', 1, 1, 0, 'system', 'system'
WHERE NOT EXISTS (SELECT 1 FROM permission_templates WHERE company_id IS NULL AND template_code = 'SYS_CASHIER');
INSERT OR IGNORE INTO permission_template_grants (template_id, permission_id)
SELECT t.template_id, p.permission_id FROM permission_templates t CROSS JOIN permissions p
WHERE t.company_id IS NULL AND t.template_code = 'SYS_CASHIER' AND p.is_active = 1 AND (p.group_code = 'POS' OR p.permission_code = 'SALES.CASH.CREATE' OR p.permission_code LIKE 'DOC.POS.%');
INSERT INTO permission_templates(company_id, template_code, name_en, name_ar, description, is_system, is_active, is_deleted, created_by, updated_by)
SELECT NULL, 'SYS_ACCOUNTANT', 'Accountant', 'Accountant', 'Accounting, vouchers and banking in full, plus report view/print/export. Seeded system template (migration 125); copy it to adapt.', 1, 1, 0, 'system', 'system'
WHERE NOT EXISTS (SELECT 1 FROM permission_templates WHERE company_id IS NULL AND template_code = 'SYS_ACCOUNTANT');
INSERT OR IGNORE INTO permission_template_grants (template_id, permission_id)
SELECT t.template_id, p.permission_id FROM permission_templates t CROSS JOIN permissions p
WHERE t.company_id IS NULL AND t.template_code = 'SYS_ACCOUNTANT' AND p.is_active = 1 AND (p.group_code IN ('ACCOUNTING','VOUCHERS','BANKING') OR p.permission_code IN ('REPORT.VIEW','REPORT.PRINT','REPORT.EXPORT'));
