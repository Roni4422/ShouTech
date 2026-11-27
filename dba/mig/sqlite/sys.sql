-- @shoutech-migration 000_history.sql DA224A3C54335A428BCCBBBF5B140B5FD66E2CBCEB54651ACCCEF3017DA61AA0 05D12E1C47031AC2494A26CF4A39124BA79089A19A6E3791B32A5088EFA1720A 05D12E1C47031AC2494A26CF4A39124BA79089A19A6E3791B32A5088EFA1720A 05D12E1C47031AC2494A26CF4A39124BA79089A19A6E3791B32A5088EFA1720A
-- Migration-runner bootstrap history. Keep this file immutable after release.
CREATE TABLE IF NOT EXISTS schema_migrations (
    migration_id TEXT PRIMARY KEY,
    checksum TEXT NOT NULL,
    applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
-- @shoutech-migration 001_sqlite_core.sql 13D2F8309DA331D0219E02E705C0C0471F9CF6415F43F05546A6B3DD83F39C17 A96DAE29790EE76EBA89E96144C8B34C390A9305C00199B4D914E441CBD038B9 A96DAE29790EE76EBA89E96144C8B34C390A9305C00199B4D914E441CBD038B9 A96DAE29790EE76EBA89E96144C8B34C390A9305C00199B4D914E441CBD038B9
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS companies (
    company_id TEXT NOT NULL PRIMARY KEY,
    company_code TEXT NOT NULL UNIQUE,
    company_name_ar TEXT NOT NULL,
    company_name_en TEXT NOT NULL,
    email TEXT,
    phone TEXT,
    mobile TEXT,
    address_line1 TEXT,
    commercial_registration TEXT,
    currency_code TEXT NOT NULL DEFAULT 'USD',
    language_code TEXT NOT NULL DEFAULT 'ar',
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS users (
    user_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    username TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    password_salt TEXT NOT NULL,
    full_name TEXT NOT NULL,
    full_name_ar TEXT NOT NULL,
    full_name_en TEXT NOT NULL,
    email TEXT,
    must_change_password INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(company_id, username),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS fiscal_years (
    fiscal_year_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    fiscal_year INTEGER NOT NULL,
    database_name TEXT NOT NULL,
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL,
    is_current INTEGER NOT NULL DEFAULT 1,
    is_closed INTEGER NOT NULL DEFAULT 0,
    UNIQUE(company_id, fiscal_year),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS roles (
    role_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    role_code TEXT NOT NULL,
    role_name TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    UNIQUE(company_id, role_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS permissions (
    permission_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    permission_code TEXT NOT NULL UNIQUE,
    permission_name TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS user_roles (
    user_id TEXT NOT NULL,
    role_id INTEGER NOT NULL,
    PRIMARY KEY (user_id, role_id),
    FOREIGN KEY(user_id) REFERENCES users(user_id),
    FOREIGN KEY(role_id) REFERENCES roles(role_id)
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_id INTEGER NOT NULL,
    permission_id INTEGER NOT NULL,
    PRIMARY KEY (role_id, permission_id),
    FOREIGN KEY(role_id) REFERENCES roles(role_id),
    FOREIGN KEY(permission_id) REFERENCES permissions(permission_id)
);

CREATE TABLE IF NOT EXISTS units_of_measure (
    uom_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    uom_code TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    UNIQUE(company_id, uom_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);

CREATE TABLE IF NOT EXISTS products (
    product_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    product_code TEXT NOT NULL,
    product_name TEXT NOT NULL,
    category_id INTEGER NOT NULL,
    primary_uom_id INTEGER NOT NULL,
    default_sale_price NUMERIC NOT NULL DEFAULT 0,
    default_purchase_price NUMERIC NOT NULL DEFAULT 0,
    cost_price NUMERIC NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by TEXT,
    updated_at TEXT,
    deleted_by TEXT,
    deleted_at TEXT,
    UNIQUE(company_id, product_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(primary_uom_id) REFERENCES units_of_measure(uom_id)
);

CREATE TABLE IF NOT EXISTS journal_entries (
    journal_entry_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    entry_date TEXT NOT NULL,
    description TEXT NOT NULL,
    status INTEGER NOT NULL DEFAULT 0 CHECK (status IN (0, 1, 2)),
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    posted_by TEXT,
    posted_at TEXT,
    reversed_by TEXT,
    reversed_at TEXT
);

CREATE TABLE IF NOT EXISTS journal_entry_lines (
    journal_entry_line_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    journal_entry_id TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    account_id TEXT NOT NULL,
    debit NUMERIC NOT NULL DEFAULT 0,
    credit NUMERIC NOT NULL DEFAULT 0,
    cost_center TEXT,
    note TEXT,
    UNIQUE(journal_entry_id, line_number),
    CHECK (debit >= 0 AND credit >= 0 AND ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0))),
    FOREIGN KEY(journal_entry_id) REFERENCES journal_entries(journal_entry_id)
);

CREATE TABLE IF NOT EXISTS journal_audit_events (
    journal_audit_event_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    journal_entry_id TEXT NOT NULL,
    event_type INTEGER NOT NULL CHECK (event_type IN (1, 2, 3)),
    actor TEXT NOT NULL,
    event_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    details TEXT,
    FOREIGN KEY(journal_entry_id) REFERENCES journal_entries(journal_entry_id)
);

CREATE TABLE IF NOT EXISTS stock_balances (
    stock_balance_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    warehouse_id INTEGER NOT NULL,
    product_id INTEGER NOT NULL,
    available_qty NUMERIC(18,6) NOT NULL DEFAULT 0,
    last_cost NUMERIC,
    last_updated TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    UNIQUE (company_id, warehouse_id, product_id)
);

CREATE TABLE IF NOT EXISTS stock_movements (
    movement_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    movement_type TEXT NOT NULL,
    movement_date TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    warehouse_id_to INTEGER,
    product_id INTEGER NOT NULL,
    quantity NUMERIC(18,6) NOT NULL CHECK (quantity > 0),
    unit_cost NUMERIC,
    reference_table TEXT,
    notes TEXT,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
-- @shoutech-migration 002_rbac.sql DB05C85259BE2C1D5D6734FC58756A220C79A4369675B1B9E07629B6D1F81286 EE23036BF1FE63FE63FB1DB0A1E6BBAACB8FAA3FFA7EF537FA0D609E67F300BC EE23036BF1FE63FE63FB1DB0A1E6BBAACB8FAA3FFA7EF537FA0D609E67F300BC EE23036BF1FE63FE63FB1DB0A1E6BBAACB8FAA3FFA7EF537FA0D609E67F300BC
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
-- @shoutech-migration 003_roles.sql 2757912662E197DBCF034F8B6E1ACF7F9D40B5BEACF290E45D3BCF8A3B368F58 F5B25A4AEB2BE93A66B902A2DD88DB36AA1FFBD07479A397EB849181B1DDCC01 F5B25A4AEB2BE93A66B902A2DD88DB36AA1FFBD07479A397EB849181B1DDCC01 F5B25A4AEB2BE93A66B902A2DD88DB36AA1FFBD07479A397EB849181B1DDCC01
-- Enterprise role model: Administrator, User, Viewer only.
-- Existing Manager/Accountant assignments are normalized to User before the legacy roles are removed.
PRAGMA foreign_keys = ON;

INSERT OR IGNORE INTO roles (company_id, role_code, role_name)
SELECT company_id, 'ROLE_USER', 'User' FROM companies;

INSERT OR IGNORE INTO roles (company_id, role_code, role_name)
SELECT company_id, 'ROLE_VIEWER', 'Viewer' FROM companies;

INSERT OR IGNORE INTO user_roles (user_id, role_id)
SELECT ur.user_id, r_user.role_id
FROM user_roles ur
INNER JOIN roles old_role ON old_role.role_id = ur.role_id
INNER JOIN users u ON u.user_id = ur.user_id
INNER JOIN roles r_user ON r_user.company_id = u.company_id AND r_user.role_code = 'ROLE_USER'
WHERE old_role.role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT');

DELETE FROM role_permissions
WHERE role_id IN (SELECT role_id FROM roles WHERE role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT'));

DELETE FROM user_roles
WHERE role_id IN (SELECT role_id FROM roles WHERE role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT'));

DELETE FROM roles
WHERE role_code IN ('ROLE_MANAGER', 'ROLE_ACCOUNTANT');

-- Baseline Viewer: read-only business visibility.
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER'
  AND p.permission_code IN ('COMPANY_VIEW','USER_VIEW','ROLE_VIEW','PERMISSION_VIEW',
                            'ACCOUNTING_VIEW','INVENTORY_VIEW','SALES_VIEW','PURCHASE_VIEW',
                            'REPORT_VIEW','SYSTEM_VIEW')
  AND p.is_active = 1;

-- Baseline User: business operations, without security/role administration.
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER'
  AND p.permission_code IN ('COMPANY_VIEW','ACCOUNTING_VIEW','ACCOUNTING_EDIT','JOURNAL_POST',
                            'INVENTORY_VIEW','INVENTORY_EDIT','SALES_VIEW','SALES_EDIT',
                            'PURCHASE_VIEW','PURCHASE_EDIT','REPORT_VIEW')
  AND p.is_active = 1;

-- Administrator remains the only built-in role with full current permissions.
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1;
-- @shoutech-migration 004_permissions.sql 4E4E9487AFC9DDB7FBD6ED0D9F46D0ED16A2BC145E18B8F642B05E0D468E2C29 5C6F0777176ECF85C0D2D1CB0E0FFFCCCE1D430D315609667AE559850D5B2F29 5C6F0777176ECF85C0D2D1CB0E0FFFCCCE1D430D315609667AE559850D5B2F29 5C6F0777176ECF85C0D2D1CB0E0FFFCCCE1D430D315609667AE559850D5B2F29
-- Enterprise permission catalog v4: tabbed, granular permissions + per-user overrides.
PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS permission_groups (
    group_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    group_code TEXT NOT NULL UNIQUE,
    name_ar TEXT NOT NULL, name_en TEXT NOT NULL,
    display_order INTEGER NOT NULL DEFAULT 0, is_active INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS user_permissions (
    user_id TEXT NOT NULL, permission_id INTEGER NOT NULL, effect INTEGER NOT NULL CHECK (effect IN (0,1)),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_id, permission_id),
    FOREIGN KEY(user_id) REFERENCES users(user_id), FOREIGN KEY(permission_id) REFERENCES permissions(permission_id)
);
CREATE INDEX IF NOT EXISTS ix_user_permissions_permission ON user_permissions(permission_id);
ALTER TABLE permissions ADD COLUMN group_code TEXT NOT NULL DEFAULT 'SYSTEM';
ALTER TABLE permissions ADD COLUMN feature_code TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN operation_code TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN name_ar TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN name_en TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN description TEXT;
ALTER TABLE permissions ADD COLUMN display_order INTEGER NOT NULL DEFAULT 0;
ALTER TABLE permissions ADD COLUMN is_sensitive INTEGER NOT NULL DEFAULT 0;
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('SYSTEM','النظام','System',1);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('COMPANY','الشركات','Companies',2);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('INVENTORY','المخزون','Inventory',3);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('ACCOUNTING','المحاسبة','Accounting',4);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('VOUCHERS','السندات','Vouchers',5);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('SALES','المبيعات','Sales',6);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('PURCHASING','المشتريات','Purchasing',7);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('POS','نقاط البيع','Point of Sale',8);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('CUSTOMERS','العملاء','Customers',9);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('REPRESENTATIVES','المندوبون','Representatives',10);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('MANUFACTURING','التصنيع','Manufacturing',11);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('BANKING','البنوك','Banking',12);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('DELIVERY','التوصيل','Delivery',13);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('REPORTING','التقارير','Reports',14);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('DATA','البيانات','Data',15);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('BACKUP','النسخ الاحتياطي','Backup',16);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('AUDIT','التدقيق','Audit',17);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('LICENSING','الترخيص','Licensing',18);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('UPDATES','التحديثات','Updates',19);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('AI','الذكاء الاصطناعي','AI',20);
INSERT OR IGNORE INTO permission_groups(group_code,name_ar,name_en,display_order) VALUES ('HELP','المساعدة','Help',21);
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.ADMIN','Admin - Manage','SYSTEM','ADMIN','MANAGE','إدارة النظام - إدارة','Admin - Manage','صلاحية تشغيلية ضمن قسم النظام',1,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='ADMIN',operation_code='MANAGE',name_ar='إدارة النظام - إدارة',name_en='Admin - Manage',display_order=1,is_sensitive=1 WHERE permission_code='SYS.ADMIN';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.SETTINGS.VIEW','Settings - View','SYSTEM','SETTINGS','VIEW','الإعدادات - عرض','Settings - View','صلاحية تشغيلية ضمن قسم النظام',2,0);
UPDATE permissions SET group_code='SYSTEM',feature_code='SETTINGS',operation_code='VIEW',name_ar='الإعدادات - عرض',name_en='Settings - View',display_order=2,is_sensitive=0 WHERE permission_code='SYS.SETTINGS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.SETTINGS.UPDATE','Settings - Update','SYSTEM','SETTINGS','UPDATE','الإعدادات - تعديل','Settings - Update','صلاحية تشغيلية ضمن قسم النظام',3,0);
UPDATE permissions SET group_code='SYSTEM',feature_code='SETTINGS',operation_code='UPDATE',name_ar='الإعدادات - تعديل',name_en='Settings - Update',display_order=3,is_sensitive=0 WHERE permission_code='SYS.SETTINGS.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.USERS.VIEW','Users - View','SYSTEM','USERS','VIEW','المستخدمون - عرض','Users - View','صلاحية تشغيلية ضمن قسم النظام',4,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='USERS',operation_code='VIEW',name_ar='المستخدمون - عرض',name_en='Users - View',display_order=4,is_sensitive=1 WHERE permission_code='SYS.USERS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.USERS.CREATE','Users - Create','SYSTEM','USERS','CREATE','المستخدمون - إنشاء','Users - Create','صلاحية تشغيلية ضمن قسم النظام',5,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='USERS',operation_code='CREATE',name_ar='المستخدمون - إنشاء',name_en='Users - Create',display_order=5,is_sensitive=1 WHERE permission_code='SYS.USERS.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.USERS.UPDATE','Users - Update','SYSTEM','USERS','UPDATE','المستخدمون - تعديل','Users - Update','صلاحية تشغيلية ضمن قسم النظام',6,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='USERS',operation_code='UPDATE',name_ar='المستخدمون - تعديل',name_en='Users - Update',display_order=6,is_sensitive=1 WHERE permission_code='SYS.USERS.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.USERS.DISABLE','Users - Disable','SYSTEM','USERS','DISABLE','المستخدمون - تعطيل','Users - Disable','صلاحية تشغيلية ضمن قسم النظام',7,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='USERS',operation_code='DISABLE',name_ar='المستخدمون - تعطيل',name_en='Users - Disable',display_order=7,is_sensitive=1 WHERE permission_code='SYS.USERS.DISABLE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.USERS.DELETE','Users - Delete','SYSTEM','USERS','DELETE','المستخدمون - حذف','Users - Delete','صلاحية تشغيلية ضمن قسم النظام',8,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='USERS',operation_code='DELETE',name_ar='المستخدمون - حذف',name_en='Users - Delete',display_order=8,is_sensitive=1 WHERE permission_code='SYS.USERS.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.ROLES.VIEW','Roles - View','SYSTEM','ROLES','VIEW','الأدوار - عرض','Roles - View','صلاحية تشغيلية ضمن قسم النظام',9,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='ROLES',operation_code='VIEW',name_ar='الأدوار - عرض',name_en='Roles - View',display_order=9,is_sensitive=1 WHERE permission_code='SYS.ROLES.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.ROLES.CREATE','Roles - Create','SYSTEM','ROLES','CREATE','الأدوار - إنشاء','Roles - Create','صلاحية تشغيلية ضمن قسم النظام',10,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='ROLES',operation_code='CREATE',name_ar='الأدوار - إنشاء',name_en='Roles - Create',display_order=10,is_sensitive=1 WHERE permission_code='SYS.ROLES.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.ROLES.UPDATE','Roles - Update','SYSTEM','ROLES','UPDATE','الأدوار - تعديل','Roles - Update','صلاحية تشغيلية ضمن قسم النظام',11,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='ROLES',operation_code='UPDATE',name_ar='الأدوار - تعديل',name_en='Roles - Update',display_order=11,is_sensitive=1 WHERE permission_code='SYS.ROLES.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.ROLES.DELETE','Roles - Delete','SYSTEM','ROLES','DELETE','الأدوار - حذف','Roles - Delete','صلاحية تشغيلية ضمن قسم النظام',12,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='ROLES',operation_code='DELETE',name_ar='الأدوار - حذف',name_en='Roles - Delete',display_order=12,is_sensitive=1 WHERE permission_code='SYS.ROLES.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.PERMISSIONS.VIEW','Permissions - View','SYSTEM','PERMISSIONS','VIEW','الصلاحيات - عرض','Permissions - View','صلاحية تشغيلية ضمن قسم النظام',13,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='PERMISSIONS',operation_code='VIEW',name_ar='الصلاحيات - عرض',name_en='Permissions - View',display_order=13,is_sensitive=1 WHERE permission_code='SYS.PERMISSIONS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.MAINTENANCE','Maintenance - Manage','SYSTEM','MAINTENANCE','MANAGE','الصيانة - إدارة','Maintenance - Manage','صلاحية تشغيلية ضمن قسم النظام',14,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='MAINTENANCE',operation_code='MANAGE',name_ar='الصيانة - إدارة',name_en='Maintenance - Manage',display_order=14,is_sensitive=1 WHERE permission_code='SYS.MAINTENANCE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.PLUGINS.MANAGE','Plugins - Manage','SYSTEM','PLUGINS','MANAGE','الإضافات - إدارة','Plugins - Manage','صلاحية تشغيلية ضمن قسم النظام',15,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='PLUGINS',operation_code='MANAGE',name_ar='الإضافات - إدارة',name_en='Plugins - Manage',display_order=15,is_sensitive=1 WHERE permission_code='SYS.PLUGINS.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.COMPANY.VIEW','Company - View','COMPANY','COMPANY','VIEW','الشركة - عرض','Company - View','صلاحية تشغيلية ضمن قسم الشركات',16,0);
UPDATE permissions SET group_code='COMPANY',feature_code='COMPANY',operation_code='VIEW',name_ar='الشركة - عرض',name_en='Company - View',display_order=16,is_sensitive=0 WHERE permission_code='ORG.COMPANY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.COMPANY.CREATE','Company - Create','COMPANY','COMPANY','CREATE','الشركة - إنشاء','Company - Create','صلاحية تشغيلية ضمن قسم الشركات',17,1);
UPDATE permissions SET group_code='COMPANY',feature_code='COMPANY',operation_code='CREATE',name_ar='الشركة - إنشاء',name_en='Company - Create',display_order=17,is_sensitive=1 WHERE permission_code='ORG.COMPANY.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.COMPANY.UPDATE','Company - Update','COMPANY','COMPANY','UPDATE','الشركة - تعديل','Company - Update','صلاحية تشغيلية ضمن قسم الشركات',18,1);
UPDATE permissions SET group_code='COMPANY',feature_code='COMPANY',operation_code='UPDATE',name_ar='الشركة - تعديل',name_en='Company - Update',display_order=18,is_sensitive=1 WHERE permission_code='ORG.COMPANY.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.COMPANY.DELETE','Company - Delete','COMPANY','COMPANY','DELETE','الشركة - حذف','Company - Delete','صلاحية تشغيلية ضمن قسم الشركات',19,1);
UPDATE permissions SET group_code='COMPANY',feature_code='COMPANY',operation_code='DELETE',name_ar='الشركة - حذف',name_en='Company - Delete',display_order=19,is_sensitive=1 WHERE permission_code='ORG.COMPANY.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.BRANCH.VIEW','Branch - View','COMPANY','BRANCH','VIEW','الفروع - عرض','Branch - View','صلاحية تشغيلية ضمن قسم الشركات',20,0);
UPDATE permissions SET group_code='COMPANY',feature_code='BRANCH',operation_code='VIEW',name_ar='الفروع - عرض',name_en='Branch - View',display_order=20,is_sensitive=0 WHERE permission_code='ORG.BRANCH.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.BRANCH.CREATE','Branch - Create','COMPANY','BRANCH','CREATE','الفروع - إنشاء','Branch - Create','صلاحية تشغيلية ضمن قسم الشركات',21,0);
UPDATE permissions SET group_code='COMPANY',feature_code='BRANCH',operation_code='CREATE',name_ar='الفروع - إنشاء',name_en='Branch - Create',display_order=21,is_sensitive=0 WHERE permission_code='ORG.BRANCH.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.BRANCH.UPDATE','Branch - Update','COMPANY','BRANCH','UPDATE','الفروع - تعديل','Branch - Update','صلاحية تشغيلية ضمن قسم الشركات',22,0);
UPDATE permissions SET group_code='COMPANY',feature_code='BRANCH',operation_code='UPDATE',name_ar='الفروع - تعديل',name_en='Branch - Update',display_order=22,is_sensitive=0 WHERE permission_code='ORG.BRANCH.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.BRANCH.DELETE','Branch - Delete','COMPANY','BRANCH','DELETE','الفروع - حذف','Branch - Delete','صلاحية تشغيلية ضمن قسم الشركات',23,1);
UPDATE permissions SET group_code='COMPANY',feature_code='BRANCH',operation_code='DELETE',name_ar='الفروع - حذف',name_en='Branch - Delete',display_order=23,is_sensitive=1 WHERE permission_code='ORG.BRANCH.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.WAREHOUSE.VIEW','Warehouse - View','COMPANY','WAREHOUSE','VIEW','المستودعات - عرض','Warehouse - View','صلاحية تشغيلية ضمن قسم الشركات',24,0);
UPDATE permissions SET group_code='COMPANY',feature_code='WAREHOUSE',operation_code='VIEW',name_ar='المستودعات - عرض',name_en='Warehouse - View',display_order=24,is_sensitive=0 WHERE permission_code='ORG.WAREHOUSE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.WAREHOUSE.CREATE','Warehouse - Create','COMPANY','WAREHOUSE','CREATE','المستودعات - إنشاء','Warehouse - Create','صلاحية تشغيلية ضمن قسم الشركات',25,0);
UPDATE permissions SET group_code='COMPANY',feature_code='WAREHOUSE',operation_code='CREATE',name_ar='المستودعات - إنشاء',name_en='Warehouse - Create',display_order=25,is_sensitive=0 WHERE permission_code='ORG.WAREHOUSE.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.WAREHOUSE.UPDATE','Warehouse - Update','COMPANY','WAREHOUSE','UPDATE','المستودعات - تعديل','Warehouse - Update','صلاحية تشغيلية ضمن قسم الشركات',26,0);
UPDATE permissions SET group_code='COMPANY',feature_code='WAREHOUSE',operation_code='UPDATE',name_ar='المستودعات - تعديل',name_en='Warehouse - Update',display_order=26,is_sensitive=0 WHERE permission_code='ORG.WAREHOUSE.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ORG.WAREHOUSE.DELETE','Warehouse - Delete','COMPANY','WAREHOUSE','DELETE','المستودعات - حذف','Warehouse - Delete','صلاحية تشغيلية ضمن قسم الشركات',27,1);
UPDATE permissions SET group_code='COMPANY',feature_code='WAREHOUSE',operation_code='DELETE',name_ar='المستودعات - حذف',name_en='Warehouse - Delete',display_order=27,is_sensitive=1 WHERE permission_code='ORG.WAREHOUSE.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.ITEM.VIEW','Item - View','INVENTORY','ITEM','VIEW','الأصناف - عرض','Item - View','صلاحية تشغيلية ضمن قسم المخزون',28,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='ITEM',operation_code='VIEW',name_ar='الأصناف - عرض',name_en='Item - View',display_order=28,is_sensitive=0 WHERE permission_code='INV.ITEM.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.ITEM.CREATE','Item - Create','INVENTORY','ITEM','CREATE','الأصناف - إنشاء','Item - Create','صلاحية تشغيلية ضمن قسم المخزون',29,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='ITEM',operation_code='CREATE',name_ar='الأصناف - إنشاء',name_en='Item - Create',display_order=29,is_sensitive=0 WHERE permission_code='INV.ITEM.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.ITEM.UPDATE','Item - Update','INVENTORY','ITEM','UPDATE','الأصناف - تعديل','Item - Update','صلاحية تشغيلية ضمن قسم المخزون',30,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='ITEM',operation_code='UPDATE',name_ar='الأصناف - تعديل',name_en='Item - Update',display_order=30,is_sensitive=0 WHERE permission_code='INV.ITEM.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.ITEM.DELETE','Item - Delete','INVENTORY','ITEM','DELETE','الأصناف - حذف','Item - Delete','صلاحية تشغيلية ضمن قسم المخزون',31,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='ITEM',operation_code='DELETE',name_ar='الأصناف - حذف',name_en='Item - Delete',display_order=31,is_sensitive=1 WHERE permission_code='INV.ITEM.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.CATEGORY.VIEW','Category - View','INVENTORY','CATEGORY','VIEW','التصنيفات - عرض','Category - View','صلاحية تشغيلية ضمن قسم المخزون',32,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='CATEGORY',operation_code='VIEW',name_ar='التصنيفات - عرض',name_en='Category - View',display_order=32,is_sensitive=0 WHERE permission_code='INV.CATEGORY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.CATEGORY.CREATE','Category - Create','INVENTORY','CATEGORY','CREATE','التصنيفات - إنشاء','Category - Create','صلاحية تشغيلية ضمن قسم المخزون',33,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='CATEGORY',operation_code='CREATE',name_ar='التصنيفات - إنشاء',name_en='Category - Create',display_order=33,is_sensitive=0 WHERE permission_code='INV.CATEGORY.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.CATEGORY.UPDATE','Category - Update','INVENTORY','CATEGORY','UPDATE','التصنيفات - تعديل','Category - Update','صلاحية تشغيلية ضمن قسم المخزون',34,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='CATEGORY',operation_code='UPDATE',name_ar='التصنيفات - تعديل',name_en='Category - Update',display_order=34,is_sensitive=0 WHERE permission_code='INV.CATEGORY.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.CATEGORY.DELETE','Category - Delete','INVENTORY','CATEGORY','DELETE','التصنيفات - حذف','Category - Delete','صلاحية تشغيلية ضمن قسم المخزون',35,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='CATEGORY',operation_code='DELETE',name_ar='التصنيفات - حذف',name_en='Category - Delete',display_order=35,is_sensitive=1 WHERE permission_code='INV.CATEGORY.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.WAREHOUSE.VIEW','Warehouse - View','INVENTORY','WAREHOUSE','VIEW','المستودعات - عرض','Warehouse - View','صلاحية تشغيلية ضمن قسم المخزون',36,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='WAREHOUSE',operation_code='VIEW',name_ar='المستودعات - عرض',name_en='Warehouse - View',display_order=36,is_sensitive=0 WHERE permission_code='INV.WAREHOUSE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.WAREHOUSE.CREATE','Warehouse - Create','INVENTORY','WAREHOUSE','CREATE','المستودعات - إنشاء','Warehouse - Create','صلاحية تشغيلية ضمن قسم المخزون',37,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='WAREHOUSE',operation_code='CREATE',name_ar='المستودعات - إنشاء',name_en='Warehouse - Create',display_order=37,is_sensitive=0 WHERE permission_code='INV.WAREHOUSE.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.WAREHOUSE.UPDATE','Warehouse - Update','INVENTORY','WAREHOUSE','UPDATE','المستودعات - تعديل','Warehouse - Update','صلاحية تشغيلية ضمن قسم المخزون',38,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='WAREHOUSE',operation_code='UPDATE',name_ar='المستودعات - تعديل',name_en='Warehouse - Update',display_order=38,is_sensitive=0 WHERE permission_code='INV.WAREHOUSE.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.WAREHOUSE.DELETE','Warehouse - Delete','INVENTORY','WAREHOUSE','DELETE','المستودعات - حذف','Warehouse - Delete','صلاحية تشغيلية ضمن قسم المخزون',39,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='WAREHOUSE',operation_code='DELETE',name_ar='المستودعات - حذف',name_en='Warehouse - Delete',display_order=39,is_sensitive=1 WHERE permission_code='INV.WAREHOUSE.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.STOCK.VIEW','Stock - View','INVENTORY','STOCK','VIEW','المخزون - عرض','Stock - View','صلاحية تشغيلية ضمن قسم المخزون',40,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='STOCK',operation_code='VIEW',name_ar='المخزون - عرض',name_en='Stock - View',display_order=40,is_sensitive=0 WHERE permission_code='INV.STOCK.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.STOCK.TRANSFER','Stock - Transfer','INVENTORY','STOCK','TRANSFER','المخزون - تحويل','Stock - Transfer','صلاحية تشغيلية ضمن قسم المخزون',41,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='STOCK',operation_code='TRANSFER',name_ar='المخزون - تحويل',name_en='Stock - Transfer',display_order=41,is_sensitive=0 WHERE permission_code='INV.STOCK.TRANSFER';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.STOCK.ADJUST','Stock - Adjust','INVENTORY','STOCK','ADJUST','المخزون - تسوية','Stock - Adjust','صلاحية تشغيلية ضمن قسم المخزون',42,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='STOCK',operation_code='ADJUST',name_ar='المخزون - تسوية',name_en='Stock - Adjust',display_order=42,is_sensitive=0 WHERE permission_code='INV.STOCK.ADJUST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.STOCK.COUNT','Stock - Count','INVENTORY','STOCK','COUNT','المخزون - جرد','Stock - Count','صلاحية تشغيلية ضمن قسم المخزون',43,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='STOCK',operation_code='COUNT',name_ar='المخزون - جرد',name_en='Stock - Count',display_order=43,is_sensitive=0 WHERE permission_code='INV.STOCK.COUNT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.STOCK.POST','Stock - Post','INVENTORY','STOCK','POST','المخزون - ترحيل/اعتماد','Stock - Post','صلاحية تشغيلية ضمن قسم المخزون',44,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='STOCK',operation_code='POST',name_ar='المخزون - ترحيل/اعتماد',name_en='Stock - Post',display_order=44,is_sensitive=1 WHERE permission_code='INV.STOCK.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BARCODE.VIEW','Barcode - View','INVENTORY','BARCODE','VIEW','الباركود - عرض','Barcode - View','صلاحية تشغيلية ضمن قسم المخزون',45,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='BARCODE',operation_code='VIEW',name_ar='الباركود - عرض',name_en='Barcode - View',display_order=45,is_sensitive=0 WHERE permission_code='INV.BARCODE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BARCODE.GENERATE','Barcode - Generate','INVENTORY','BARCODE','GENERATE','الباركود - توليد','Barcode - Generate','صلاحية تشغيلية ضمن قسم المخزون',46,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='BARCODE',operation_code='GENERATE',name_ar='الباركود - توليد',name_en='Barcode - Generate',display_order=46,is_sensitive=0 WHERE permission_code='INV.BARCODE.GENERATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BARCODE.DESIGN','Barcode - Design','INVENTORY','BARCODE','DESIGN','الباركود - تصميم','Barcode - Design','صلاحية تشغيلية ضمن قسم المخزون',47,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='BARCODE',operation_code='DESIGN',name_ar='الباركود - تصميم',name_en='Barcode - Design',display_order=47,is_sensitive=0 WHERE permission_code='INV.BARCODE.DESIGN';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.BARCODE.PRINT','Barcode - Print','INVENTORY','BARCODE','PRINT','الباركود - طباعة','Barcode - Print','صلاحية تشغيلية ضمن قسم المخزون',48,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='BARCODE',operation_code='PRINT',name_ar='الباركود - طباعة',name_en='Barcode - Print',display_order=48,is_sensitive=0 WHERE permission_code='INV.BARCODE.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.PRICELIST.VIEW','Pricelist - View','INVENTORY','PRICELIST','VIEW','قوائم الأسعار - عرض','Pricelist - View','صلاحية تشغيلية ضمن قسم المخزون',49,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='PRICELIST',operation_code='VIEW',name_ar='قوائم الأسعار - عرض',name_en='Pricelist - View',display_order=49,is_sensitive=0 WHERE permission_code='INV.PRICELIST.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.PRICELIST.CREATE','Pricelist - Create','INVENTORY','PRICELIST','CREATE','قوائم الأسعار - إنشاء','Pricelist - Create','صلاحية تشغيلية ضمن قسم المخزون',50,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='PRICELIST',operation_code='CREATE',name_ar='قوائم الأسعار - إنشاء',name_en='Pricelist - Create',display_order=50,is_sensitive=0 WHERE permission_code='INV.PRICELIST.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.PRICELIST.UPDATE','Pricelist - Update','INVENTORY','PRICELIST','UPDATE','قوائم الأسعار - تعديل','Pricelist - Update','صلاحية تشغيلية ضمن قسم المخزون',51,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='PRICELIST',operation_code='UPDATE',name_ar='قوائم الأسعار - تعديل',name_en='Pricelist - Update',display_order=51,is_sensitive=0 WHERE permission_code='INV.PRICELIST.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.SERIAL.VIEW','Serial - View','INVENTORY','SERIAL','VIEW','الأرقام التسلسلية - عرض','Serial - View','صلاحية تشغيلية ضمن قسم المخزون',52,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='SERIAL',operation_code='VIEW',name_ar='الأرقام التسلسلية - عرض',name_en='Serial - View',display_order=52,is_sensitive=0 WHERE permission_code='INV.SERIAL.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.SERIAL.UPDATE','Serial - Update','INVENTORY','SERIAL','UPDATE','الأرقام التسلسلية - تعديل','Serial - Update','صلاحية تشغيلية ضمن قسم المخزون',53,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='SERIAL',operation_code='UPDATE',name_ar='الأرقام التسلسلية - تعديل',name_en='Serial - Update',display_order=53,is_sensitive=0 WHERE permission_code='INV.SERIAL.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.EXPIRY.VIEW','Expiry - View','INVENTORY','EXPIRY','VIEW','الصلاحية - عرض','Expiry - View','صلاحية تشغيلية ضمن قسم المخزون',54,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='EXPIRY',operation_code='VIEW',name_ar='الصلاحية - عرض',name_en='Expiry - View',display_order=54,is_sensitive=0 WHERE permission_code='INV.EXPIRY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.DISCOUNT.VIEW','Discount - View','INVENTORY','DISCOUNT','VIEW','الخصومات - عرض','Discount - View','صلاحية تشغيلية ضمن قسم المخزون',55,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='DISCOUNT',operation_code='VIEW',name_ar='الخصومات - عرض',name_en='Discount - View',display_order=55,is_sensitive=0 WHERE permission_code='INV.DISCOUNT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.DISCOUNT.MANAGE','Discount - Manage','INVENTORY','DISCOUNT','MANAGE','الخصومات - إدارة','Discount - Manage','صلاحية تشغيلية ضمن قسم المخزون',56,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='DISCOUNT',operation_code='MANAGE',name_ar='الخصومات - إدارة',name_en='Discount - Manage',display_order=56,is_sensitive=1 WHERE permission_code='INV.DISCOUNT.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.ACCOUNT.VIEW','Account - View','ACCOUNTING','ACCOUNT','VIEW','الحسابات - عرض','Account - View','صلاحية تشغيلية ضمن قسم المحاسبة',57,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACCOUNT',operation_code='VIEW',name_ar='الحسابات - عرض',name_en='Account - View',display_order=57,is_sensitive=0 WHERE permission_code='ACC.ACCOUNT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.ACCOUNT.CREATE','Account - Create','ACCOUNTING','ACCOUNT','CREATE','الحسابات - إنشاء','Account - Create','صلاحية تشغيلية ضمن قسم المحاسبة',58,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACCOUNT',operation_code='CREATE',name_ar='الحسابات - إنشاء',name_en='Account - Create',display_order=58,is_sensitive=0 WHERE permission_code='ACC.ACCOUNT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.ACCOUNT.UPDATE','Account - Update','ACCOUNTING','ACCOUNT','UPDATE','الحسابات - تعديل','Account - Update','صلاحية تشغيلية ضمن قسم المحاسبة',59,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACCOUNT',operation_code='UPDATE',name_ar='الحسابات - تعديل',name_en='Account - Update',display_order=59,is_sensitive=0 WHERE permission_code='ACC.ACCOUNT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.ACCOUNT.DELETE','Account - Delete','ACCOUNTING','ACCOUNT','DELETE','الحسابات - حذف','Account - Delete','صلاحية تشغيلية ضمن قسم المحاسبة',60,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACCOUNT',operation_code='DELETE',name_ar='الحسابات - حذف',name_en='Account - Delete',display_order=60,is_sensitive=1 WHERE permission_code='ACC.ACCOUNT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.JOURNAL.VIEW','Journal - View','ACCOUNTING','JOURNAL','VIEW','اليوميات - عرض','Journal - View','صلاحية تشغيلية ضمن قسم المحاسبة',61,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='JOURNAL',operation_code='VIEW',name_ar='اليوميات - عرض',name_en='Journal - View',display_order=61,is_sensitive=0 WHERE permission_code='ACC.JOURNAL.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.JOURNAL.CREATE','Journal - Create','ACCOUNTING','JOURNAL','CREATE','اليوميات - إنشاء','Journal - Create','صلاحية تشغيلية ضمن قسم المحاسبة',62,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='JOURNAL',operation_code='CREATE',name_ar='اليوميات - إنشاء',name_en='Journal - Create',display_order=62,is_sensitive=0 WHERE permission_code='ACC.JOURNAL.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.JOURNAL.UPDATE','Journal - Update','ACCOUNTING','JOURNAL','UPDATE','اليوميات - تعديل','Journal - Update','صلاحية تشغيلية ضمن قسم المحاسبة',63,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='JOURNAL',operation_code='UPDATE',name_ar='اليوميات - تعديل',name_en='Journal - Update',display_order=63,is_sensitive=0 WHERE permission_code='ACC.JOURNAL.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.JOURNAL.POST','Journal - Post','ACCOUNTING','JOURNAL','POST','اليوميات - ترحيل/اعتماد','Journal - Post','صلاحية تشغيلية ضمن قسم المحاسبة',64,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='JOURNAL',operation_code='POST',name_ar='اليوميات - ترحيل/اعتماد',name_en='Journal - Post',display_order=64,is_sensitive=1 WHERE permission_code='ACC.JOURNAL.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.JOURNAL.REVERSE','Journal - Reverse','ACCOUNTING','JOURNAL','REVERSE','اليوميات - عكس/إلغاء الترحيل','Journal - Reverse','صلاحية تشغيلية ضمن قسم المحاسبة',65,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='JOURNAL',operation_code='REVERSE',name_ar='اليوميات - عكس/إلغاء الترحيل',name_en='Journal - Reverse',display_order=65,is_sensitive=1 WHERE permission_code='ACC.JOURNAL.REVERSE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.LEDGER.VIEW','Ledger - View','ACCOUNTING','LEDGER','VIEW','الأستاذ العام - عرض','Ledger - View','صلاحية تشغيلية ضمن قسم المحاسبة',66,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='LEDGER',operation_code='VIEW',name_ar='الأستاذ العام - عرض',name_en='Ledger - View',display_order=66,is_sensitive=0 WHERE permission_code='ACC.LEDGER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.COSTCENTER.VIEW','Costcenter - View','ACCOUNTING','COSTCENTER','VIEW','مراكز التكلفة - عرض','Costcenter - View','صلاحية تشغيلية ضمن قسم المحاسبة',67,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='COSTCENTER',operation_code='VIEW',name_ar='مراكز التكلفة - عرض',name_en='Costcenter - View',display_order=67,is_sensitive=0 WHERE permission_code='ACC.COSTCENTER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.COSTCENTER.MANAGE','Costcenter - Manage','ACCOUNTING','COSTCENTER','MANAGE','مراكز التكلفة - إدارة','Costcenter - Manage','صلاحية تشغيلية ضمن قسم المحاسبة',68,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='COSTCENTER',operation_code='MANAGE',name_ar='مراكز التكلفة - إدارة',name_en='Costcenter - Manage',display_order=68,is_sensitive=1 WHERE permission_code='ACC.COSTCENTER.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.CURRENCY.VIEW','Currency - View','ACCOUNTING','CURRENCY','VIEW','العملات - عرض','Currency - View','صلاحية تشغيلية ضمن قسم المحاسبة',69,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='CURRENCY',operation_code='VIEW',name_ar='العملات - عرض',name_en='Currency - View',display_order=69,is_sensitive=0 WHERE permission_code='ACC.CURRENCY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.CURRENCY.MANAGE','Currency - Manage','ACCOUNTING','CURRENCY','MANAGE','العملات - إدارة','Currency - Manage','صلاحية تشغيلية ضمن قسم المحاسبة',70,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='CURRENCY',operation_code='MANAGE',name_ar='العملات - إدارة',name_en='Currency - Manage',display_order=70,is_sensitive=1 WHERE permission_code='ACC.CURRENCY.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.EXCHANGE_RATE.VIEW','Exchange Rate - View','ACCOUNTING','EXCHANGE_RATE','VIEW','أسعار الصرف - عرض','Exchange Rate - View','صلاحية تشغيلية ضمن قسم المحاسبة',71,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='EXCHANGE_RATE',operation_code='VIEW',name_ar='أسعار الصرف - عرض',name_en='Exchange Rate - View',display_order=71,is_sensitive=0 WHERE permission_code='ACC.EXCHANGE_RATE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.EXCHANGE_RATE.MANAGE','Exchange Rate - Manage','ACCOUNTING','EXCHANGE_RATE','MANAGE','أسعار الصرف - إدارة','Exchange Rate - Manage','صلاحية تشغيلية ضمن قسم المحاسبة',72,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='EXCHANGE_RATE',operation_code='MANAGE',name_ar='أسعار الصرف - إدارة',name_en='Exchange Rate - Manage',display_order=72,is_sensitive=1 WHERE permission_code='ACC.EXCHANGE_RATE.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.TRIAL_BALANCE.VIEW','Trial Balance - View','ACCOUNTING','TRIAL_BALANCE','VIEW','ميزان المراجعة - عرض','Trial Balance - View','صلاحية تشغيلية ضمن قسم المحاسبة',73,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='TRIAL_BALANCE',operation_code='VIEW',name_ar='ميزان المراجعة - عرض',name_en='Trial Balance - View',display_order=73,is_sensitive=0 WHERE permission_code='ACC.TRIAL_BALANCE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.VOUCHER.VIEW','Voucher - View','VOUCHERS','VOUCHER','VIEW','السندات - عرض','Voucher - View','صلاحية تشغيلية ضمن قسم السندات',74,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='VOUCHER',operation_code='VIEW',name_ar='السندات - عرض',name_en='Voucher - View',display_order=74,is_sensitive=0 WHERE permission_code='VCH.VOUCHER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.VOUCHER.CREATE','Voucher - Create','VOUCHERS','VOUCHER','CREATE','السندات - إنشاء','Voucher - Create','صلاحية تشغيلية ضمن قسم السندات',75,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='VOUCHER',operation_code='CREATE',name_ar='السندات - إنشاء',name_en='Voucher - Create',display_order=75,is_sensitive=0 WHERE permission_code='VCH.VOUCHER.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.VOUCHER.UPDATE','Voucher - Update','VOUCHERS','VOUCHER','UPDATE','السندات - تعديل','Voucher - Update','صلاحية تشغيلية ضمن قسم السندات',76,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='VOUCHER',operation_code='UPDATE',name_ar='السندات - تعديل',name_en='Voucher - Update',display_order=76,is_sensitive=0 WHERE permission_code='VCH.VOUCHER.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.VOUCHER.DELETE','Voucher - Delete','VOUCHERS','VOUCHER','DELETE','السندات - حذف','Voucher - Delete','صلاحية تشغيلية ضمن قسم السندات',77,1);
UPDATE permissions SET group_code='VOUCHERS',feature_code='VOUCHER',operation_code='DELETE',name_ar='السندات - حذف',name_en='Voucher - Delete',display_order=77,is_sensitive=1 WHERE permission_code='VCH.VOUCHER.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.VOUCHER.POST','Voucher - Post','VOUCHERS','VOUCHER','POST','السندات - ترحيل/اعتماد','Voucher - Post','صلاحية تشغيلية ضمن قسم السندات',78,1);
UPDATE permissions SET group_code='VOUCHERS',feature_code='VOUCHER',operation_code='POST',name_ar='السندات - ترحيل/اعتماد',name_en='Voucher - Post',display_order=78,is_sensitive=1 WHERE permission_code='VCH.VOUCHER.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.VOUCHER.REVERSE','Voucher - Reverse','VOUCHERS','VOUCHER','REVERSE','السندات - عكس/إلغاء الترحيل','Voucher - Reverse','صلاحية تشغيلية ضمن قسم السندات',79,1);
UPDATE permissions SET group_code='VOUCHERS',feature_code='VOUCHER',operation_code='REVERSE',name_ar='السندات - عكس/إلغاء الترحيل',name_en='Voucher - Reverse',display_order=79,is_sensitive=1 WHERE permission_code='VCH.VOUCHER.REVERSE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.RECEIPT.CREATE','Receipt - Create','VOUCHERS','RECEIPT','CREATE','القبض - إنشاء','Receipt - Create','صلاحية تشغيلية ضمن قسم السندات',80,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='RECEIPT',operation_code='CREATE',name_ar='القبض - إنشاء',name_en='Receipt - Create',display_order=80,is_sensitive=0 WHERE permission_code='VCH.RECEIPT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.PAYMENT.CREATE','Payment - Create','VOUCHERS','PAYMENT','CREATE','الدفع - إنشاء','Payment - Create','صلاحية تشغيلية ضمن قسم السندات',81,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='PAYMENT',operation_code='CREATE',name_ar='الدفع - إنشاء',name_en='Payment - Create',display_order=81,is_sensitive=0 WHERE permission_code='VCH.PAYMENT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.SECURITY.VIEW','Security - View','VOUCHERS','SECURITY','VIEW','الأوراق المالية - عرض','Security - View','صلاحية تشغيلية ضمن قسم السندات',82,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='SECURITY',operation_code='VIEW',name_ar='الأوراق المالية - عرض',name_en='Security - View',display_order=82,is_sensitive=0 WHERE permission_code='VCH.SECURITY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.SECURITY.MANAGE','Security - Manage','VOUCHERS','SECURITY','MANAGE','الأوراق المالية - إدارة','Security - Manage','صلاحية تشغيلية ضمن قسم السندات',83,1);
UPDATE permissions SET group_code='VOUCHERS',feature_code='SECURITY',operation_code='MANAGE',name_ar='الأوراق المالية - إدارة',name_en='Security - Manage',display_order=83,is_sensitive=1 WHERE permission_code='VCH.SECURITY.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.INSTALLMENT.VIEW','Installment - View','VOUCHERS','INSTALLMENT','VIEW','الأقساط - عرض','Installment - View','صلاحية تشغيلية ضمن قسم السندات',84,0);
UPDATE permissions SET group_code='VOUCHERS',feature_code='INSTALLMENT',operation_code='VIEW',name_ar='الأقساط - عرض',name_en='Installment - View',display_order=84,is_sensitive=0 WHERE permission_code='VCH.INSTALLMENT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('VCH.INSTALLMENT.MANAGE','Installment - Manage','VOUCHERS','INSTALLMENT','MANAGE','الأقساط - إدارة','Installment - Manage','صلاحية تشغيلية ضمن قسم السندات',85,1);
UPDATE permissions SET group_code='VOUCHERS',feature_code='INSTALLMENT',operation_code='MANAGE',name_ar='الأقساط - إدارة',name_en='Installment - Manage',display_order=85,is_sensitive=1 WHERE permission_code='VCH.INSTALLMENT.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.VIEW','Invoice - View','SALES','INVOICE','VIEW','الفواتير - عرض','Invoice - View','صلاحية تشغيلية ضمن قسم المبيعات',86,0);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='VIEW',name_ar='الفواتير - عرض',name_en='Invoice - View',display_order=86,is_sensitive=0 WHERE permission_code='SALES.INVOICE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.CREATE','Invoice - Create','SALES','INVOICE','CREATE','الفواتير - إنشاء','Invoice - Create','صلاحية تشغيلية ضمن قسم المبيعات',87,0);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='CREATE',name_ar='الفواتير - إنشاء',name_en='Invoice - Create',display_order=87,is_sensitive=0 WHERE permission_code='SALES.INVOICE.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.UPDATE','Invoice - Update','SALES','INVOICE','UPDATE','الفواتير - تعديل','Invoice - Update','صلاحية تشغيلية ضمن قسم المبيعات',88,0);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='UPDATE',name_ar='الفواتير - تعديل',name_en='Invoice - Update',display_order=88,is_sensitive=0 WHERE permission_code='SALES.INVOICE.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.DELETE','Invoice - Delete','SALES','INVOICE','DELETE','الفواتير - حذف','Invoice - Delete','صلاحية تشغيلية ضمن قسم المبيعات',89,1);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='DELETE',name_ar='الفواتير - حذف',name_en='Invoice - Delete',display_order=89,is_sensitive=1 WHERE permission_code='SALES.INVOICE.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.POST','Invoice - Post','SALES','INVOICE','POST','الفواتير - ترحيل/اعتماد','Invoice - Post','صلاحية تشغيلية ضمن قسم المبيعات',90,1);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='POST',name_ar='الفواتير - ترحيل/اعتماد',name_en='Invoice - Post',display_order=90,is_sensitive=1 WHERE permission_code='SALES.INVOICE.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.REVERSE','Invoice - Reverse','SALES','INVOICE','REVERSE','الفواتير - عكس/إلغاء الترحيل','Invoice - Reverse','صلاحية تشغيلية ضمن قسم المبيعات',91,1);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='REVERSE',name_ar='الفواتير - عكس/إلغاء الترحيل',name_en='Invoice - Reverse',display_order=91,is_sensitive=1 WHERE permission_code='SALES.INVOICE.REVERSE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RETURN.VIEW','Return - View','SALES','RETURN','VIEW','المرتجعات - عرض','Return - View','صلاحية تشغيلية ضمن قسم المبيعات',92,0);
UPDATE permissions SET group_code='SALES',feature_code='RETURN',operation_code='VIEW',name_ar='المرتجعات - عرض',name_en='Return - View',display_order=92,is_sensitive=0 WHERE permission_code='SALES.RETURN.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RETURN.CREATE','Return - Create','SALES','RETURN','CREATE','المرتجعات - إنشاء','Return - Create','صلاحية تشغيلية ضمن قسم المبيعات',93,0);
UPDATE permissions SET group_code='SALES',feature_code='RETURN',operation_code='CREATE',name_ar='المرتجعات - إنشاء',name_en='Return - Create',display_order=93,is_sensitive=0 WHERE permission_code='SALES.RETURN.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RETURN.POST','Return - Post','SALES','RETURN','POST','المرتجعات - ترحيل/اعتماد','Return - Post','صلاحية تشغيلية ضمن قسم المبيعات',94,1);
UPDATE permissions SET group_code='SALES',feature_code='RETURN',operation_code='POST',name_ar='المرتجعات - ترحيل/اعتماد',name_en='Return - Post',display_order=94,is_sensitive=1 WHERE permission_code='SALES.RETURN.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.VIEW','Quotation - View','SALES','QUOTATION','VIEW','عروض الأسعار - عرض','Quotation - View','صلاحية تشغيلية ضمن قسم المبيعات',95,0);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='VIEW',name_ar='عروض الأسعار - عرض',name_en='Quotation - View',display_order=95,is_sensitive=0 WHERE permission_code='SALES.QUOTATION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.CREATE','Quotation - Create','SALES','QUOTATION','CREATE','عروض الأسعار - إنشاء','Quotation - Create','صلاحية تشغيلية ضمن قسم المبيعات',96,0);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='CREATE',name_ar='عروض الأسعار - إنشاء',name_en='Quotation - Create',display_order=96,is_sensitive=0 WHERE permission_code='SALES.QUOTATION.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.UPDATE','Quotation - Update','SALES','QUOTATION','UPDATE','عروض الأسعار - تعديل','Quotation - Update','صلاحية تشغيلية ضمن قسم المبيعات',97,0);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='UPDATE',name_ar='عروض الأسعار - تعديل',name_en='Quotation - Update',display_order=97,is_sensitive=0 WHERE permission_code='SALES.QUOTATION.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.DELETE','Quotation - Delete','SALES','QUOTATION','DELETE','عروض الأسعار - حذف','Quotation - Delete','صلاحية تشغيلية ضمن قسم المبيعات',98,1);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='DELETE',name_ar='عروض الأسعار - حذف',name_en='Quotation - Delete',display_order=98,is_sensitive=1 WHERE permission_code='SALES.QUOTATION.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.CONVERT','Quotation - Convert','SALES','QUOTATION','CONVERT','عروض الأسعار - Convert','Quotation - Convert','صلاحية تشغيلية ضمن قسم المبيعات',99,0);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='CONVERT',name_ar='عروض الأسعار - Convert',name_en='Quotation - Convert',display_order=99,is_sensitive=0 WHERE permission_code='SALES.QUOTATION.CONVERT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.VIEW','Order - View','SALES','ORDER','VIEW','الطلبات - عرض','Order - View','صلاحية تشغيلية ضمن قسم المبيعات',100,0);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='VIEW',name_ar='الطلبات - عرض',name_en='Order - View',display_order=100,is_sensitive=0 WHERE permission_code='SALES.ORDER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.CREATE','Order - Create','SALES','ORDER','CREATE','الطلبات - إنشاء','Order - Create','صلاحية تشغيلية ضمن قسم المبيعات',101,0);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='CREATE',name_ar='الطلبات - إنشاء',name_en='Order - Create',display_order=101,is_sensitive=0 WHERE permission_code='SALES.ORDER.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.UPDATE','Order - Update','SALES','ORDER','UPDATE','الطلبات - تعديل','Order - Update','صلاحية تشغيلية ضمن قسم المبيعات',102,0);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='UPDATE',name_ar='الطلبات - تعديل',name_en='Order - Update',display_order=102,is_sensitive=0 WHERE permission_code='SALES.ORDER.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.CANCEL','Order - Cancel','SALES','ORDER','CANCEL','الطلبات - إلغاء','Order - Cancel','صلاحية تشغيلية ضمن قسم المبيعات',103,0);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='CANCEL',name_ar='الطلبات - إلغاء',name_en='Order - Cancel',display_order=103,is_sensitive=0 WHERE permission_code='SALES.ORDER.CANCEL';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.CASH.CREATE','Cash - Create','SALES','CASH','CREATE','النقدية - إنشاء','Cash - Create','صلاحية تشغيلية ضمن قسم المبيعات',104,0);
UPDATE permissions SET group_code='SALES',feature_code='CASH',operation_code='CREATE',name_ar='النقدية - إنشاء',name_en='Cash - Create',display_order=104,is_sensitive=0 WHERE permission_code='SALES.CASH.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.PRICE.OVERRIDE','Price - Override','SALES','PRICE','OVERRIDE','الأسعار - تجاوز','Price - Override','صلاحية تشغيلية ضمن قسم المبيعات',105,1);
UPDATE permissions SET group_code='SALES',feature_code='PRICE',operation_code='OVERRIDE',name_ar='الأسعار - تجاوز',name_en='Price - Override',display_order=105,is_sensitive=1 WHERE permission_code='SALES.PRICE.OVERRIDE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.DISCOUNT.OVERRIDE','Discount - Override','SALES','DISCOUNT','OVERRIDE','الخصومات - تجاوز','Discount - Override','صلاحية تشغيلية ضمن قسم المبيعات',106,1);
UPDATE permissions SET group_code='SALES',feature_code='DISCOUNT',operation_code='OVERRIDE',name_ar='الخصومات - تجاوز',name_en='Discount - Override',display_order=106,is_sensitive=1 WHERE permission_code='SALES.DISCOUNT.OVERRIDE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.REPOST','Invoice - Repost','SALES','INVOICE','REPOST','الفواتير - Repost','Invoice - Repost','صلاحية تشغيلية ضمن قسم المبيعات',107,0);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='REPOST',name_ar='الفواتير - Repost',name_en='Invoice - Repost',display_order=107,is_sensitive=0 WHERE permission_code='SALES.INVOICE.REPOST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.REPORTS.VIEW','Reports - View','SALES','REPORTS','VIEW','التقارير - عرض','Reports - View','صلاحية تشغيلية ضمن قسم المبيعات',108,0);
UPDATE permissions SET group_code='SALES',feature_code='REPORTS',operation_code='VIEW',name_ar='التقارير - عرض',name_en='Reports - View',display_order=108,is_sensitive=0 WHERE permission_code='SALES.REPORTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.INVOICE.VIEW','Invoice - View','PURCHASING','INVOICE','VIEW','الفواتير - عرض','Invoice - View','صلاحية تشغيلية ضمن قسم المشتريات',109,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='INVOICE',operation_code='VIEW',name_ar='الفواتير - عرض',name_en='Invoice - View',display_order=109,is_sensitive=0 WHERE permission_code='PURCH.INVOICE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.INVOICE.CREATE','Invoice - Create','PURCHASING','INVOICE','CREATE','الفواتير - إنشاء','Invoice - Create','صلاحية تشغيلية ضمن قسم المشتريات',110,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='INVOICE',operation_code='CREATE',name_ar='الفواتير - إنشاء',name_en='Invoice - Create',display_order=110,is_sensitive=0 WHERE permission_code='PURCH.INVOICE.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.INVOICE.UPDATE','Invoice - Update','PURCHASING','INVOICE','UPDATE','الفواتير - تعديل','Invoice - Update','صلاحية تشغيلية ضمن قسم المشتريات',111,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='INVOICE',operation_code='UPDATE',name_ar='الفواتير - تعديل',name_en='Invoice - Update',display_order=111,is_sensitive=0 WHERE permission_code='PURCH.INVOICE.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.INVOICE.DELETE','Invoice - Delete','PURCHASING','INVOICE','DELETE','الفواتير - حذف','Invoice - Delete','صلاحية تشغيلية ضمن قسم المشتريات',112,1);
UPDATE permissions SET group_code='PURCHASING',feature_code='INVOICE',operation_code='DELETE',name_ar='الفواتير - حذف',name_en='Invoice - Delete',display_order=112,is_sensitive=1 WHERE permission_code='PURCH.INVOICE.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.INVOICE.POST','Invoice - Post','PURCHASING','INVOICE','POST','الفواتير - ترحيل/اعتماد','Invoice - Post','صلاحية تشغيلية ضمن قسم المشتريات',113,1);
UPDATE permissions SET group_code='PURCHASING',feature_code='INVOICE',operation_code='POST',name_ar='الفواتير - ترحيل/اعتماد',name_en='Invoice - Post',display_order=113,is_sensitive=1 WHERE permission_code='PURCH.INVOICE.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.RETURN.VIEW','Return - View','PURCHASING','RETURN','VIEW','المرتجعات - عرض','Return - View','صلاحية تشغيلية ضمن قسم المشتريات',114,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='RETURN',operation_code='VIEW',name_ar='المرتجعات - عرض',name_en='Return - View',display_order=114,is_sensitive=0 WHERE permission_code='PURCH.RETURN.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.RETURN.CREATE','Return - Create','PURCHASING','RETURN','CREATE','المرتجعات - إنشاء','Return - Create','صلاحية تشغيلية ضمن قسم المشتريات',115,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='RETURN',operation_code='CREATE',name_ar='المرتجعات - إنشاء',name_en='Return - Create',display_order=115,is_sensitive=0 WHERE permission_code='PURCH.RETURN.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.RETURN.POST','Return - Post','PURCHASING','RETURN','POST','المرتجعات - ترحيل/اعتماد','Return - Post','صلاحية تشغيلية ضمن قسم المشتريات',116,1);
UPDATE permissions SET group_code='PURCHASING',feature_code='RETURN',operation_code='POST',name_ar='المرتجعات - ترحيل/اعتماد',name_en='Return - Post',display_order=116,is_sensitive=1 WHERE permission_code='PURCH.RETURN.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.ORDER.VIEW','Order - View','PURCHASING','ORDER','VIEW','الطلبات - عرض','Order - View','صلاحية تشغيلية ضمن قسم المشتريات',117,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='ORDER',operation_code='VIEW',name_ar='الطلبات - عرض',name_en='Order - View',display_order=117,is_sensitive=0 WHERE permission_code='PURCH.ORDER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.ORDER.CREATE','Order - Create','PURCHASING','ORDER','CREATE','الطلبات - إنشاء','Order - Create','صلاحية تشغيلية ضمن قسم المشتريات',118,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='ORDER',operation_code='CREATE',name_ar='الطلبات - إنشاء',name_en='Order - Create',display_order=118,is_sensitive=0 WHERE permission_code='PURCH.ORDER.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.ORDER.UPDATE','Order - Update','PURCHASING','ORDER','UPDATE','الطلبات - تعديل','Order - Update','صلاحية تشغيلية ضمن قسم المشتريات',119,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='ORDER',operation_code='UPDATE',name_ar='الطلبات - تعديل',name_en='Order - Update',display_order=119,is_sensitive=0 WHERE permission_code='PURCH.ORDER.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.ORDER.CANCEL','Order - Cancel','PURCHASING','ORDER','CANCEL','الطلبات - إلغاء','Order - Cancel','صلاحية تشغيلية ضمن قسم المشتريات',120,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='ORDER',operation_code='CANCEL',name_ar='الطلبات - إلغاء',name_en='Order - Cancel',display_order=120,is_sensitive=0 WHERE permission_code='PURCH.ORDER.CANCEL';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.REPORTS.VIEW','Reports - View','PURCHASING','REPORTS','VIEW','التقارير - عرض','Reports - View','صلاحية تشغيلية ضمن قسم المشتريات',121,0);
UPDATE permissions SET group_code='PURCHASING',feature_code='REPORTS',operation_code='VIEW',name_ar='التقارير - عرض',name_en='Reports - View',display_order=121,is_sensitive=0 WHERE permission_code='PURCH.REPORTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.TERMINAL.VIEW','Terminal - View','POS','TERMINAL','VIEW','أجهزة نقاط البيع - عرض','Terminal - View','صلاحية تشغيلية ضمن قسم نقاط البيع',122,0);
UPDATE permissions SET group_code='POS',feature_code='TERMINAL',operation_code='VIEW',name_ar='أجهزة نقاط البيع - عرض',name_en='Terminal - View',display_order=122,is_sensitive=0 WHERE permission_code='POS.TERMINAL.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.TERMINAL.MANAGE','Terminal - Manage','POS','TERMINAL','MANAGE','أجهزة نقاط البيع - إدارة','Terminal - Manage','صلاحية تشغيلية ضمن قسم نقاط البيع',123,1);
UPDATE permissions SET group_code='POS',feature_code='TERMINAL',operation_code='MANAGE',name_ar='أجهزة نقاط البيع - إدارة',name_en='Terminal - Manage',display_order=123,is_sensitive=1 WHERE permission_code='POS.TERMINAL.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.SESSION.OPEN','Session - Open','POS','SESSION','OPEN','الجلسات - فتح','Session - Open','صلاحية تشغيلية ضمن قسم نقاط البيع',124,0);
UPDATE permissions SET group_code='POS',feature_code='SESSION',operation_code='OPEN',name_ar='الجلسات - فتح',name_en='Session - Open',display_order=124,is_sensitive=0 WHERE permission_code='POS.SESSION.OPEN';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.SESSION.CLOSE','Session - Close','POS','SESSION','CLOSE','الجلسات - إغلاق','Session - Close','صلاحية تشغيلية ضمن قسم نقاط البيع',125,0);
UPDATE permissions SET group_code='POS',feature_code='SESSION',operation_code='CLOSE',name_ar='الجلسات - إغلاق',name_en='Session - Close',display_order=125,is_sensitive=0 WHERE permission_code='POS.SESSION.CLOSE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.SESSION.REOPEN','Session - Reopen','POS','SESSION','REOPEN','الجلسات - إعادة فتح','Session - Reopen','صلاحية تشغيلية ضمن قسم نقاط البيع',126,1);
UPDATE permissions SET group_code='POS',feature_code='SESSION',operation_code='REOPEN',name_ar='الجلسات - إعادة فتح',name_en='Session - Reopen',display_order=126,is_sensitive=1 WHERE permission_code='POS.SESSION.REOPEN';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.INVOICE.CREATE','Invoice - Create','POS','INVOICE','CREATE','الفواتير - إنشاء','Invoice - Create','صلاحية تشغيلية ضمن قسم نقاط البيع',127,0);
UPDATE permissions SET group_code='POS',feature_code='INVOICE',operation_code='CREATE',name_ar='الفواتير - إنشاء',name_en='Invoice - Create',display_order=127,is_sensitive=0 WHERE permission_code='POS.INVOICE.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.INVOICE.DELETE','Invoice - Delete','POS','INVOICE','DELETE','الفواتير - حذف','Invoice - Delete','صلاحية تشغيلية ضمن قسم نقاط البيع',128,1);
UPDATE permissions SET group_code='POS',feature_code='INVOICE',operation_code='DELETE',name_ar='الفواتير - حذف',name_en='Invoice - Delete',display_order=128,is_sensitive=1 WHERE permission_code='POS.INVOICE.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.INVOICE.REFUND','Invoice - Refund','POS','INVOICE','REFUND','الفواتير - استرجاع','Invoice - Refund','صلاحية تشغيلية ضمن قسم نقاط البيع',129,0);
UPDATE permissions SET group_code='POS',feature_code='INVOICE',operation_code='REFUND',name_ar='الفواتير - استرجاع',name_en='Invoice - Refund',display_order=129,is_sensitive=0 WHERE permission_code='POS.INVOICE.REFUND';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.PRICE.OVERRIDE','Price - Override','POS','PRICE','OVERRIDE','الأسعار - تجاوز','Price - Override','صلاحية تشغيلية ضمن قسم نقاط البيع',130,1);
UPDATE permissions SET group_code='POS',feature_code='PRICE',operation_code='OVERRIDE',name_ar='الأسعار - تجاوز',name_en='Price - Override',display_order=130,is_sensitive=1 WHERE permission_code='POS.PRICE.OVERRIDE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.QUANTITY.OVERRIDE','Quantity - Override','POS','QUANTITY','OVERRIDE','Quantity - تجاوز','Quantity - Override','صلاحية تشغيلية ضمن قسم نقاط البيع',131,1);
UPDATE permissions SET group_code='POS',feature_code='QUANTITY',operation_code='OVERRIDE',name_ar='Quantity - تجاوز',name_en='Quantity - Override',display_order=131,is_sensitive=1 WHERE permission_code='POS.QUANTITY.OVERRIDE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.ITEM.DELETE','Item - Delete','POS','ITEM','DELETE','الأصناف - حذف','Item - Delete','صلاحية تشغيلية ضمن قسم نقاط البيع',132,1);
UPDATE permissions SET group_code='POS',feature_code='ITEM',operation_code='DELETE',name_ar='الأصناف - حذف',name_en='Item - Delete',display_order=132,is_sensitive=1 WHERE permission_code='POS.ITEM.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.DISCOUNT.MANAGE','Discount - Manage','POS','DISCOUNT','MANAGE','الخصومات - إدارة','Discount - Manage','صلاحية تشغيلية ضمن قسم نقاط البيع',133,1);
UPDATE permissions SET group_code='POS',feature_code='DISCOUNT',operation_code='MANAGE',name_ar='الخصومات - إدارة',name_en='Discount - Manage',display_order=133,is_sensitive=1 WHERE permission_code='POS.DISCOUNT.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.SYNC.EXECUTE','Sync - Execute','POS','SYNC','EXECUTE','Sync - تنفيذ','Sync - Execute','صلاحية تشغيلية ضمن قسم نقاط البيع',134,0);
UPDATE permissions SET group_code='POS',feature_code='SYNC',operation_code='EXECUTE',name_ar='Sync - تنفيذ',name_en='Sync - Execute',display_order=134,is_sensitive=0 WHERE permission_code='POS.SYNC.EXECUTE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.REPORTS.VIEW','Reports - View','POS','REPORTS','VIEW','التقارير - عرض','Reports - View','صلاحية تشغيلية ضمن قسم نقاط البيع',135,0);
UPDATE permissions SET group_code='POS',feature_code='REPORTS',operation_code='VIEW',name_ar='التقارير - عرض',name_en='Reports - View',display_order=135,is_sensitive=0 WHERE permission_code='POS.REPORTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER.VIEW','Customer - View','CUSTOMERS','CUSTOMER','VIEW','العملاء - عرض','Customer - View','صلاحية تشغيلية ضمن قسم العملاء',136,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER',operation_code='VIEW',name_ar='العملاء - عرض',name_en='Customer - View',display_order=136,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER.CREATE','Customer - Create','CUSTOMERS','CUSTOMER','CREATE','العملاء - إنشاء','Customer - Create','صلاحية تشغيلية ضمن قسم العملاء',137,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER',operation_code='CREATE',name_ar='العملاء - إنشاء',name_en='Customer - Create',display_order=137,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER.UPDATE','Customer - Update','CUSTOMERS','CUSTOMER','UPDATE','العملاء - تعديل','Customer - Update','صلاحية تشغيلية ضمن قسم العملاء',138,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER',operation_code='UPDATE',name_ar='العملاء - تعديل',name_en='Customer - Update',display_order=138,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER.DELETE','Customer - Delete','CUSTOMERS','CUSTOMER','DELETE','العملاء - حذف','Customer - Delete','صلاحية تشغيلية ضمن قسم العملاء',139,1);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER',operation_code='DELETE',name_ar='العملاء - حذف',name_en='Customer - Delete',display_order=139,is_sensitive=1 WHERE permission_code='CRM.CUSTOMER.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_PRICING.VIEW','Customer Pricing - View','CUSTOMERS','CUSTOMER_PRICING','VIEW','تسعير العملاء - عرض','Customer Pricing - View','صلاحية تشغيلية ضمن قسم العملاء',140,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_PRICING',operation_code='VIEW',name_ar='تسعير العملاء - عرض',name_en='Customer Pricing - View',display_order=140,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER_PRICING.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_PRICING.MANAGE','Customer Pricing - Manage','CUSTOMERS','CUSTOMER_PRICING','MANAGE','تسعير العملاء - إدارة','Customer Pricing - Manage','صلاحية تشغيلية ضمن قسم العملاء',141,1);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_PRICING',operation_code='MANAGE',name_ar='تسعير العملاء - إدارة',name_en='Customer Pricing - Manage',display_order=141,is_sensitive=1 WHERE permission_code='CRM.CUSTOMER_PRICING.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_DISCOUNT.VIEW','Customer Discount - View','CUSTOMERS','CUSTOMER_DISCOUNT','VIEW','خصومات العملاء - عرض','Customer Discount - View','صلاحية تشغيلية ضمن قسم العملاء',142,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_DISCOUNT',operation_code='VIEW',name_ar='خصومات العملاء - عرض',name_en='Customer Discount - View',display_order=142,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER_DISCOUNT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_DISCOUNT.MANAGE','Customer Discount - Manage','CUSTOMERS','CUSTOMER_DISCOUNT','MANAGE','خصومات العملاء - إدارة','Customer Discount - Manage','صلاحية تشغيلية ضمن قسم العملاء',143,1);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_DISCOUNT',operation_code='MANAGE',name_ar='خصومات العملاء - إدارة',name_en='Customer Discount - Manage',display_order=143,is_sensitive=1 WHERE permission_code='CRM.CUSTOMER_DISCOUNT.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_BALANCE.VIEW','Customer Balance - View','CUSTOMERS','CUSTOMER_BALANCE','VIEW','أرصدة العملاء - عرض','Customer Balance - View','صلاحية تشغيلية ضمن قسم العملاء',144,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_BALANCE',operation_code='VIEW',name_ar='أرصدة العملاء - عرض',name_en='Customer Balance - View',display_order=144,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER_BALANCE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.RECEIVABLE.VIEW','Receivable - View','CUSTOMERS','RECEIVABLE','VIEW','الذمم المدينة - عرض','Receivable - View','صلاحية تشغيلية ضمن قسم العملاء',145,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='RECEIVABLE',operation_code='VIEW',name_ar='الذمم المدينة - عرض',name_en='Receivable - View',display_order=145,is_sensitive=0 WHERE permission_code='CRM.RECEIVABLE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.RECEIVABLE.MANAGE','Receivable - Manage','CUSTOMERS','RECEIVABLE','MANAGE','الذمم المدينة - إدارة','Receivable - Manage','صلاحية تشغيلية ضمن قسم العملاء',146,1);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='RECEIVABLE',operation_code='MANAGE',name_ar='الذمم المدينة - إدارة',name_en='Receivable - Manage',display_order=146,is_sensitive=1 WHERE permission_code='CRM.RECEIVABLE.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_TARGET.VIEW','Customer Target - View','CUSTOMERS','CUSTOMER_TARGET','VIEW','أهداف العملاء - عرض','Customer Target - View','صلاحية تشغيلية ضمن قسم العملاء',147,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_TARGET',operation_code='VIEW',name_ar='أهداف العملاء - عرض',name_en='Customer Target - View',display_order=147,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER_TARGET.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_TARGET.MANAGE','Customer Target - Manage','CUSTOMERS','CUSTOMER_TARGET','MANAGE','أهداف العملاء - إدارة','Customer Target - Manage','صلاحية تشغيلية ضمن قسم العملاء',148,1);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_TARGET',operation_code='MANAGE',name_ar='أهداف العملاء - إدارة',name_en='Customer Target - Manage',display_order=148,is_sensitive=1 WHERE permission_code='CRM.CUSTOMER_TARGET.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CUSTOMER_REPORTS.VIEW','Customer Reports - View','CUSTOMERS','CUSTOMER_REPORTS','VIEW','Customer Reports - عرض','Customer Reports - View','صلاحية تشغيلية ضمن قسم العملاء',149,0);
UPDATE permissions SET group_code='CUSTOMERS',feature_code='CUSTOMER_REPORTS',operation_code='VIEW',name_ar='Customer Reports - عرض',name_en='Customer Reports - View',display_order=149,is_sensitive=0 WHERE permission_code='CRM.CUSTOMER_REPORTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.REPRESENTATIVE.VIEW','Representative - View','REPRESENTATIVES','REPRESENTATIVE','VIEW','المندوبون - عرض','Representative - View','صلاحية تشغيلية ضمن قسم المندوبون',150,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='REPRESENTATIVE',operation_code='VIEW',name_ar='المندوبون - عرض',name_en='Representative - View',display_order=150,is_sensitive=0 WHERE permission_code='REP.REPRESENTATIVE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.REPRESENTATIVE.CREATE','Representative - Create','REPRESENTATIVES','REPRESENTATIVE','CREATE','المندوبون - إنشاء','Representative - Create','صلاحية تشغيلية ضمن قسم المندوبون',151,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='REPRESENTATIVE',operation_code='CREATE',name_ar='المندوبون - إنشاء',name_en='Representative - Create',display_order=151,is_sensitive=0 WHERE permission_code='REP.REPRESENTATIVE.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.REPRESENTATIVE.UPDATE','Representative - Update','REPRESENTATIVES','REPRESENTATIVE','UPDATE','المندوبون - تعديل','Representative - Update','صلاحية تشغيلية ضمن قسم المندوبون',152,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='REPRESENTATIVE',operation_code='UPDATE',name_ar='المندوبون - تعديل',name_en='Representative - Update',display_order=152,is_sensitive=0 WHERE permission_code='REP.REPRESENTATIVE.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.REPRESENTATIVE.DELETE','Representative - Delete','REPRESENTATIVES','REPRESENTATIVE','DELETE','المندوبون - حذف','Representative - Delete','صلاحية تشغيلية ضمن قسم المندوبون',153,1);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='REPRESENTATIVE',operation_code='DELETE',name_ar='المندوبون - حذف',name_en='Representative - Delete',display_order=153,is_sensitive=1 WHERE permission_code='REP.REPRESENTATIVE.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.TARGET.VIEW','Target - View','REPRESENTATIVES','TARGET','VIEW','الأهداف - عرض','Target - View','صلاحية تشغيلية ضمن قسم المندوبون',154,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='TARGET',operation_code='VIEW',name_ar='الأهداف - عرض',name_en='Target - View',display_order=154,is_sensitive=0 WHERE permission_code='REP.TARGET.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.TARGET.MANAGE','Target - Manage','REPRESENTATIVES','TARGET','MANAGE','الأهداف - إدارة','Target - Manage','صلاحية تشغيلية ضمن قسم المندوبون',155,1);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='TARGET',operation_code='MANAGE',name_ar='الأهداف - إدارة',name_en='Target - Manage',display_order=155,is_sensitive=1 WHERE permission_code='REP.TARGET.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.COLLECTION.VIEW','Collection - View','REPRESENTATIVES','COLLECTION','VIEW','التحصيل - عرض','Collection - View','صلاحية تشغيلية ضمن قسم المندوبون',156,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='COLLECTION',operation_code='VIEW',name_ar='التحصيل - عرض',name_en='Collection - View',display_order=156,is_sensitive=0 WHERE permission_code='REP.COLLECTION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.COLLECTION.MANAGE','Collection - Manage','REPRESENTATIVES','COLLECTION','MANAGE','التحصيل - إدارة','Collection - Manage','صلاحية تشغيلية ضمن قسم المندوبون',157,1);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='COLLECTION',operation_code='MANAGE',name_ar='التحصيل - إدارة',name_en='Collection - Manage',display_order=157,is_sensitive=1 WHERE permission_code='REP.COLLECTION.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.DEVICE.VIEW','Device - View','REPRESENTATIVES','DEVICE','VIEW','الأجهزة - عرض','Device - View','صلاحية تشغيلية ضمن قسم المندوبون',158,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='DEVICE',operation_code='VIEW',name_ar='الأجهزة - عرض',name_en='Device - View',display_order=158,is_sensitive=0 WHERE permission_code='REP.DEVICE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.DEVICE.MANAGE','Device - Manage','REPRESENTATIVES','DEVICE','MANAGE','الأجهزة - إدارة','Device - Manage','صلاحية تشغيلية ضمن قسم المندوبون',159,1);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='DEVICE',operation_code='MANAGE',name_ar='الأجهزة - إدارة',name_en='Device - Manage',display_order=159,is_sensitive=1 WHERE permission_code='REP.DEVICE.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.ROUTE.VIEW','Route - View','REPRESENTATIVES','ROUTE','VIEW','المسارات - عرض','Route - View','صلاحية تشغيلية ضمن قسم المندوبون',160,0);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='ROUTE',operation_code='VIEW',name_ar='المسارات - عرض',name_en='Route - View',display_order=160,is_sensitive=0 WHERE permission_code='REP.ROUTE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REP.ROUTE.MANAGE','Route - Manage','REPRESENTATIVES','ROUTE','MANAGE','المسارات - إدارة','Route - Manage','صلاحية تشغيلية ضمن قسم المندوبون',161,1);
UPDATE permissions SET group_code='REPRESENTATIVES',feature_code='ROUTE',operation_code='MANAGE',name_ar='المسارات - إدارة',name_en='Route - Manage',display_order=161,is_sensitive=1 WHERE permission_code='REP.ROUTE.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.BOM.VIEW','Bom - View','MANUFACTURING','BOM','VIEW','قوائم المواد - عرض','Bom - View','صلاحية تشغيلية ضمن قسم التصنيع',162,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='BOM',operation_code='VIEW',name_ar='قوائم المواد - عرض',name_en='Bom - View',display_order=162,is_sensitive=0 WHERE permission_code='MFG.BOM.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.BOM.MANAGE','Bom - Manage','MANUFACTURING','BOM','MANAGE','قوائم المواد - إدارة','Bom - Manage','صلاحية تشغيلية ضمن قسم التصنيع',163,1);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='BOM',operation_code='MANAGE',name_ar='قوائم المواد - إدارة',name_en='Bom - Manage',display_order=163,is_sensitive=1 WHERE permission_code='MFG.BOM.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.ORDER.VIEW','Order - View','MANUFACTURING','ORDER','VIEW','الطلبات - عرض','Order - View','صلاحية تشغيلية ضمن قسم التصنيع',164,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='ORDER',operation_code='VIEW',name_ar='الطلبات - عرض',name_en='Order - View',display_order=164,is_sensitive=0 WHERE permission_code='MFG.ORDER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.ORDER.CREATE','Order - Create','MANUFACTURING','ORDER','CREATE','الطلبات - إنشاء','Order - Create','صلاحية تشغيلية ضمن قسم التصنيع',165,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='ORDER',operation_code='CREATE',name_ar='الطلبات - إنشاء',name_en='Order - Create',display_order=165,is_sensitive=0 WHERE permission_code='MFG.ORDER.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.ORDER.UPDATE','Order - Update','MANUFACTURING','ORDER','UPDATE','الطلبات - تعديل','Order - Update','صلاحية تشغيلية ضمن قسم التصنيع',166,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='ORDER',operation_code='UPDATE',name_ar='الطلبات - تعديل',name_en='Order - Update',display_order=166,is_sensitive=0 WHERE permission_code='MFG.ORDER.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.ORDER.CANCEL','Order - Cancel','MANUFACTURING','ORDER','CANCEL','الطلبات - إلغاء','Order - Cancel','صلاحية تشغيلية ضمن قسم التصنيع',167,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='ORDER',operation_code='CANCEL',name_ar='الطلبات - إلغاء',name_en='Order - Cancel',display_order=167,is_sensitive=0 WHERE permission_code='MFG.ORDER.CANCEL';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PRODUCTION.VIEW','Production - View','MANUFACTURING','PRODUCTION','VIEW','الإنتاج - عرض','Production - View','صلاحية تشغيلية ضمن قسم التصنيع',168,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PRODUCTION',operation_code='VIEW',name_ar='الإنتاج - عرض',name_en='Production - View',display_order=168,is_sensitive=0 WHERE permission_code='MFG.PRODUCTION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PRODUCTION.EXECUTE','Production - Execute','MANUFACTURING','PRODUCTION','EXECUTE','الإنتاج - تنفيذ','Production - Execute','صلاحية تشغيلية ضمن قسم التصنيع',169,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PRODUCTION',operation_code='EXECUTE',name_ar='الإنتاج - تنفيذ',name_en='Production - Execute',display_order=169,is_sensitive=0 WHERE permission_code='MFG.PRODUCTION.EXECUTE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PRODUCTION.POST','Production - Post','MANUFACTURING','PRODUCTION','POST','الإنتاج - ترحيل/اعتماد','Production - Post','صلاحية تشغيلية ضمن قسم التصنيع',170,1);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PRODUCTION',operation_code='POST',name_ar='الإنتاج - ترحيل/اعتماد',name_en='Production - Post',display_order=170,is_sensitive=1 WHERE permission_code='MFG.PRODUCTION.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.PRODUCTION.REPOST','Production - Repost','MANUFACTURING','PRODUCTION','REPOST','الإنتاج - Repost','Production - Repost','صلاحية تشغيلية ضمن قسم التصنيع',171,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='PRODUCTION',operation_code='REPOST',name_ar='الإنتاج - Repost',name_en='Production - Repost',display_order=171,is_sensitive=0 WHERE permission_code='MFG.PRODUCTION.REPOST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.COST.VIEW','Cost - View','MANUFACTURING','COST','VIEW','التكلفة - عرض','Cost - View','صلاحية تشغيلية ضمن قسم التصنيع',172,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='COST',operation_code='VIEW',name_ar='التكلفة - عرض',name_en='Cost - View',display_order=172,is_sensitive=0 WHERE permission_code='MFG.COST.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.VARIANCE.VIEW','Variance - View','MANUFACTURING','VARIANCE','VIEW','الانحرافات - عرض','Variance - View','صلاحية تشغيلية ضمن قسم التصنيع',173,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='VARIANCE',operation_code='VIEW',name_ar='الانحرافات - عرض',name_en='Variance - View',display_order=173,is_sensitive=0 WHERE permission_code='MFG.VARIANCE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('MFG.REPORTS.VIEW','Reports - View','MANUFACTURING','REPORTS','VIEW','التقارير - عرض','Reports - View','صلاحية تشغيلية ضمن قسم التصنيع',174,0);
UPDATE permissions SET group_code='MANUFACTURING',feature_code='REPORTS',operation_code='VIEW',name_ar='التقارير - عرض',name_en='Reports - View',display_order=174,is_sensitive=0 WHERE permission_code='MFG.REPORTS.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.BANK.VIEW','Bank - View','BANKING','BANK','VIEW','البنوك - عرض','Bank - View','صلاحية تشغيلية ضمن قسم البنوك',175,0);
UPDATE permissions SET group_code='BANKING',feature_code='BANK',operation_code='VIEW',name_ar='البنوك - عرض',name_en='Bank - View',display_order=175,is_sensitive=0 WHERE permission_code='BANK.BANK.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.BANK.MANAGE','Bank - Manage','BANKING','BANK','MANAGE','البنوك - إدارة','Bank - Manage','صلاحية تشغيلية ضمن قسم البنوك',176,1);
UPDATE permissions SET group_code='BANKING',feature_code='BANK',operation_code='MANAGE',name_ar='البنوك - إدارة',name_en='Bank - Manage',display_order=176,is_sensitive=1 WHERE permission_code='BANK.BANK.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.ACCOUNT.VIEW','Account - View','BANKING','ACCOUNT','VIEW','الحسابات - عرض','Account - View','صلاحية تشغيلية ضمن قسم البنوك',177,0);
UPDATE permissions SET group_code='BANKING',feature_code='ACCOUNT',operation_code='VIEW',name_ar='الحسابات - عرض',name_en='Account - View',display_order=177,is_sensitive=0 WHERE permission_code='BANK.ACCOUNT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.ACCOUNT.MANAGE','Account - Manage','BANKING','ACCOUNT','MANAGE','الحسابات - إدارة','Account - Manage','صلاحية تشغيلية ضمن قسم البنوك',178,1);
UPDATE permissions SET group_code='BANKING',feature_code='ACCOUNT',operation_code='MANAGE',name_ar='الحسابات - إدارة',name_en='Account - Manage',display_order=178,is_sensitive=1 WHERE permission_code='BANK.ACCOUNT.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.BALANCE.VIEW','Balance - View','BANKING','BALANCE','VIEW','Balance - عرض','Balance - View','صلاحية تشغيلية ضمن قسم البنوك',179,0);
UPDATE permissions SET group_code='BANKING',feature_code='BALANCE',operation_code='VIEW',name_ar='Balance - عرض',name_en='Balance - View',display_order=179,is_sensitive=0 WHERE permission_code='BANK.BALANCE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BANK.FORECAST.VIEW','Forecast - View','BANKING','FORECAST','VIEW','Forecast - عرض','Forecast - View','صلاحية تشغيلية ضمن قسم البنوك',180,0);
UPDATE permissions SET group_code='BANKING',feature_code='FORECAST',operation_code='VIEW',name_ar='Forecast - عرض',name_en='Forecast - View',display_order=180,is_sensitive=0 WHERE permission_code='BANK.FORECAST.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.DRIVER.VIEW','Driver - View','DELIVERY','DRIVER','VIEW','السائقون - عرض','Driver - View','صلاحية تشغيلية ضمن قسم التوصيل',181,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='DRIVER',operation_code='VIEW',name_ar='السائقون - عرض',name_en='Driver - View',display_order=181,is_sensitive=0 WHERE permission_code='DELIVERY.DRIVER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.DRIVER.MANAGE','Driver - Manage','DELIVERY','DRIVER','MANAGE','السائقون - إدارة','Driver - Manage','صلاحية تشغيلية ضمن قسم التوصيل',182,1);
UPDATE permissions SET group_code='DELIVERY',feature_code='DRIVER',operation_code='MANAGE',name_ar='السائقون - إدارة',name_en='Driver - Manage',display_order=182,is_sensitive=1 WHERE permission_code='DELIVERY.DRIVER.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.ORDER.VIEW','Order - View','DELIVERY','ORDER','VIEW','الطلبات - عرض','Order - View','صلاحية تشغيلية ضمن قسم التوصيل',183,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='ORDER',operation_code='VIEW',name_ar='الطلبات - عرض',name_en='Order - View',display_order=183,is_sensitive=0 WHERE permission_code='DELIVERY.ORDER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.ORDER.CREATE','Order - Create','DELIVERY','ORDER','CREATE','الطلبات - إنشاء','Order - Create','صلاحية تشغيلية ضمن قسم التوصيل',184,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='ORDER',operation_code='CREATE',name_ar='الطلبات - إنشاء',name_en='Order - Create',display_order=184,is_sensitive=0 WHERE permission_code='DELIVERY.ORDER.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.ORDER.UPDATE','Order - Update','DELIVERY','ORDER','UPDATE','الطلبات - تعديل','Order - Update','صلاحية تشغيلية ضمن قسم التوصيل',185,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='ORDER',operation_code='UPDATE',name_ar='الطلبات - تعديل',name_en='Order - Update',display_order=185,is_sensitive=0 WHERE permission_code='DELIVERY.ORDER.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.ORDER.CANCEL','Order - Cancel','DELIVERY','ORDER','CANCEL','الطلبات - إلغاء','Order - Cancel','صلاحية تشغيلية ضمن قسم التوصيل',186,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='ORDER',operation_code='CANCEL',name_ar='الطلبات - إلغاء',name_en='Order - Cancel',display_order=186,is_sensitive=0 WHERE permission_code='DELIVERY.ORDER.CANCEL';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.PRICING.VIEW','Pricing - View','DELIVERY','PRICING','VIEW','التسعير - عرض','Pricing - View','صلاحية تشغيلية ضمن قسم التوصيل',187,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='PRICING',operation_code='VIEW',name_ar='التسعير - عرض',name_en='Pricing - View',display_order=187,is_sensitive=0 WHERE permission_code='DELIVERY.PRICING.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.PRICING.MANAGE','Pricing - Manage','DELIVERY','PRICING','MANAGE','التسعير - إدارة','Pricing - Manage','صلاحية تشغيلية ضمن قسم التوصيل',188,1);
UPDATE permissions SET group_code='DELIVERY',feature_code='PRICING',operation_code='MANAGE',name_ar='التسعير - إدارة',name_en='Pricing - Manage',display_order=188,is_sensitive=1 WHERE permission_code='DELIVERY.PRICING.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.TRACKING.VIEW','Tracking - View','DELIVERY','TRACKING','VIEW','التتبع - عرض','Tracking - View','صلاحية تشغيلية ضمن قسم التوصيل',189,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='TRACKING',operation_code='VIEW',name_ar='التتبع - عرض',name_en='Tracking - View',display_order=189,is_sensitive=0 WHERE permission_code='DELIVERY.TRACKING.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.CASHBOX.VIEW','Cashbox - View','DELIVERY','CASHBOX','VIEW','صندوق التوصيل - عرض','Cashbox - View','صلاحية تشغيلية ضمن قسم التوصيل',190,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='CASHBOX',operation_code='VIEW',name_ar='صندوق التوصيل - عرض',name_en='Cashbox - View',display_order=190,is_sensitive=0 WHERE permission_code='DELIVERY.CASHBOX.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DELIVERY.CASHBOX.CLOSE','Cashbox - Close','DELIVERY','CASHBOX','CLOSE','صندوق التوصيل - إغلاق','Cashbox - Close','صلاحية تشغيلية ضمن قسم التوصيل',191,0);
UPDATE permissions SET group_code='DELIVERY',feature_code='CASHBOX',operation_code='CLOSE',name_ar='صندوق التوصيل - إغلاق',name_en='Cashbox - Close',display_order=191,is_sensitive=0 WHERE permission_code='DELIVERY.CASHBOX.CLOSE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.VIEW','View - View','REPORTING','VIEW','VIEW','View - عرض','View - View','صلاحية تشغيلية ضمن قسم التقارير',192,0);
UPDATE permissions SET group_code='REPORTING',feature_code='VIEW',operation_code='VIEW',name_ar='View - عرض',name_en='View - View',display_order=192,is_sensitive=0 WHERE permission_code='REPORT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.EXPORT','Export - View','REPORTING','EXPORT','VIEW','Export - عرض','Export - View','صلاحية تشغيلية ضمن قسم التقارير',193,0);
UPDATE permissions SET group_code='REPORTING',feature_code='EXPORT',operation_code='VIEW',name_ar='Export - عرض',name_en='Export - View',display_order=193,is_sensitive=0 WHERE permission_code='REPORT.EXPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.PRINT','Print - View','REPORTING','PRINT','VIEW','Print - عرض','Print - View','صلاحية تشغيلية ضمن قسم التقارير',194,0);
UPDATE permissions SET group_code='REPORTING',feature_code='PRINT',operation_code='VIEW',name_ar='Print - عرض',name_en='Print - View',display_order=194,is_sensitive=0 WHERE permission_code='REPORT.PRINT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.DESIGNER.VIEW','Designer - View','REPORTING','DESIGNER','VIEW','مصمم التقارير - عرض','Designer - View','صلاحية تشغيلية ضمن قسم التقارير',195,0);
UPDATE permissions SET group_code='REPORTING',feature_code='DESIGNER',operation_code='VIEW',name_ar='مصمم التقارير - عرض',name_en='Designer - View',display_order=195,is_sensitive=0 WHERE permission_code='REPORT.DESIGNER.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.DESIGNER.MANAGE','Designer - Manage','REPORTING','DESIGNER','MANAGE','مصمم التقارير - إدارة','Designer - Manage','صلاحية تشغيلية ضمن قسم التقارير',196,1);
UPDATE permissions SET group_code='REPORTING',feature_code='DESIGNER',operation_code='MANAGE',name_ar='مصمم التقارير - إدارة',name_en='Designer - Manage',display_order=196,is_sensitive=1 WHERE permission_code='REPORT.DESIGNER.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.CUSTOM.VIEW','Custom - View','REPORTING','CUSTOM','VIEW','التقارير المخصصة - عرض','Custom - View','صلاحية تشغيلية ضمن قسم التقارير',197,0);
UPDATE permissions SET group_code='REPORTING',feature_code='CUSTOM',operation_code='VIEW',name_ar='التقارير المخصصة - عرض',name_en='Custom - View',display_order=197,is_sensitive=0 WHERE permission_code='REPORT.CUSTOM.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.CUSTOM.MANAGE','Custom - Manage','REPORTING','CUSTOM','MANAGE','التقارير المخصصة - إدارة','Custom - Manage','صلاحية تشغيلية ضمن قسم التقارير',198,1);
UPDATE permissions SET group_code='REPORTING',feature_code='CUSTOM',operation_code='MANAGE',name_ar='التقارير المخصصة - إدارة',name_en='Custom - Manage',display_order=198,is_sensitive=1 WHERE permission_code='REPORT.CUSTOM.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('REPORT.DASHBOARD.VIEW','Dashboard - View','REPORTING','DASHBOARD','VIEW','لوحة المعلومات - عرض','Dashboard - View','صلاحية تشغيلية ضمن قسم التقارير',199,0);
UPDATE permissions SET group_code='REPORTING',feature_code='DASHBOARD',operation_code='VIEW',name_ar='لوحة المعلومات - عرض',name_en='Dashboard - View',display_order=199,is_sensitive=0 WHERE permission_code='REPORT.DASHBOARD.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DATA.IMPORT','Import - View','DATA','IMPORT','VIEW','Import - عرض','Import - View','صلاحية تشغيلية ضمن قسم البيانات',200,0);
UPDATE permissions SET group_code='DATA',feature_code='IMPORT',operation_code='VIEW',name_ar='Import - عرض',name_en='Import - View',display_order=200,is_sensitive=0 WHERE permission_code='DATA.IMPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DATA.EXPORT','Export - View','DATA','EXPORT','VIEW','Export - عرض','Export - View','صلاحية تشغيلية ضمن قسم البيانات',201,0);
UPDATE permissions SET group_code='DATA',feature_code='EXPORT',operation_code='VIEW',name_ar='Export - عرض',name_en='Export - View',display_order=201,is_sensitive=0 WHERE permission_code='DATA.EXPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DATA.POS.IMPORT','Pos - Import','DATA','POS','IMPORT','نقاط البيع - استيراد','Pos - Import','صلاحية تشغيلية ضمن قسم البيانات',202,0);
UPDATE permissions SET group_code='DATA',feature_code='POS',operation_code='IMPORT',name_ar='نقاط البيع - استيراد',name_en='Pos - Import',display_order=202,is_sensitive=0 WHERE permission_code='DATA.POS.IMPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DATA.POS.EXPORT','Pos - Export','DATA','POS','EXPORT','نقاط البيع - تصدير','Pos - Export','صلاحية تشغيلية ضمن قسم البيانات',203,0);
UPDATE permissions SET group_code='DATA',feature_code='POS',operation_code='EXPORT',name_ar='نقاط البيع - تصدير',name_en='Pos - Export',display_order=203,is_sensitive=0 WHERE permission_code='DATA.POS.EXPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DATA.CARD.IMPORT','Card - Import','DATA','CARD','IMPORT','البطاقات - استيراد','Card - Import','صلاحية تشغيلية ضمن قسم البيانات',204,0);
UPDATE permissions SET group_code='DATA',feature_code='CARD',operation_code='IMPORT',name_ar='البطاقات - استيراد',name_en='Card - Import',display_order=204,is_sensitive=0 WHERE permission_code='DATA.CARD.IMPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('DATA.VALIDATE','Validate - View','DATA','VALIDATE','VIEW','التحقق - عرض','Validate - View','صلاحية تشغيلية ضمن قسم البيانات',205,0);
UPDATE permissions SET group_code='DATA',feature_code='VALIDATE',operation_code='VIEW',name_ar='التحقق - عرض',name_en='Validate - View',display_order=205,is_sensitive=0 WHERE permission_code='DATA.VALIDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BACKUP.VIEW','View - View','BACKUP','VIEW','VIEW','View - عرض','View - View','صلاحية تشغيلية ضمن قسم النسخ الاحتياطي',206,0);
UPDATE permissions SET group_code='BACKUP',feature_code='VIEW',operation_code='VIEW',name_ar='View - عرض',name_en='View - View',display_order=206,is_sensitive=0 WHERE permission_code='BACKUP.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BACKUP.CREATE','Create - View','BACKUP','CREATE','VIEW','Create - عرض','Create - View','صلاحية تشغيلية ضمن قسم النسخ الاحتياطي',207,0);
UPDATE permissions SET group_code='BACKUP',feature_code='CREATE',operation_code='VIEW',name_ar='Create - عرض',name_en='Create - View',display_order=207,is_sensitive=0 WHERE permission_code='BACKUP.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BACKUP.RESTORE','Restore - View','BACKUP','RESTORE','VIEW','Restore - عرض','Restore - View','صلاحية تشغيلية ضمن قسم النسخ الاحتياطي',208,0);
UPDATE permissions SET group_code='BACKUP',feature_code='RESTORE',operation_code='VIEW',name_ar='Restore - عرض',name_en='Restore - View',display_order=208,is_sensitive=0 WHERE permission_code='BACKUP.RESTORE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BACKUP.VERIFY','Verify - View','BACKUP','VERIFY','VIEW','Verify - عرض','Verify - View','صلاحية تشغيلية ضمن قسم النسخ الاحتياطي',209,0);
UPDATE permissions SET group_code='BACKUP',feature_code='VERIFY',operation_code='VIEW',name_ar='Verify - عرض',name_en='Verify - View',display_order=209,is_sensitive=0 WHERE permission_code='BACKUP.VERIFY';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BACKUP.DELETE','Delete - View','BACKUP','DELETE','VIEW','Delete - عرض','Delete - View','صلاحية تشغيلية ضمن قسم النسخ الاحتياطي',210,0);
UPDATE permissions SET group_code='BACKUP',feature_code='DELETE',operation_code='VIEW',name_ar='Delete - عرض',name_en='Delete - View',display_order=210,is_sensitive=0 WHERE permission_code='BACKUP.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('BACKUP.CONFIGURE','Configure - View','BACKUP','CONFIGURE','VIEW','Configure - عرض','Configure - View','صلاحية تشغيلية ضمن قسم النسخ الاحتياطي',211,0);
UPDATE permissions SET group_code='BACKUP',feature_code='CONFIGURE',operation_code='VIEW',name_ar='Configure - عرض',name_en='Configure - View',display_order=211,is_sensitive=0 WHERE permission_code='BACKUP.CONFIGURE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AUDIT.VIEW','View - View','AUDIT','VIEW','VIEW','View - عرض','View - View','صلاحية تشغيلية ضمن قسم التدقيق',212,0);
UPDATE permissions SET group_code='AUDIT',feature_code='VIEW',operation_code='VIEW',name_ar='View - عرض',name_en='View - View',display_order=212,is_sensitive=0 WHERE permission_code='AUDIT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AUDIT.EXPORT','Export - View','AUDIT','EXPORT','VIEW','Export - عرض','Export - View','صلاحية تشغيلية ضمن قسم التدقيق',213,0);
UPDATE permissions SET group_code='AUDIT',feature_code='EXPORT',operation_code='VIEW',name_ar='Export - عرض',name_en='Export - View',display_order=213,is_sensitive=0 WHERE permission_code='AUDIT.EXPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('LIC.LICENSE.VIEW','License - View','LICENSING','LICENSE','VIEW','الترخيص - عرض','License - View','صلاحية تشغيلية ضمن قسم الترخيص',214,0);
UPDATE permissions SET group_code='LICENSING',feature_code='LICENSE',operation_code='VIEW',name_ar='الترخيص - عرض',name_en='License - View',display_order=214,is_sensitive=0 WHERE permission_code='LIC.LICENSE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('LIC.LICENSE.RENEW','License - Renew','LICENSING','LICENSE','RENEW','الترخيص - تجديد','License - Renew','صلاحية تشغيلية ضمن قسم الترخيص',215,0);
UPDATE permissions SET group_code='LICENSING',feature_code='LICENSE',operation_code='RENEW',name_ar='الترخيص - تجديد',name_en='License - Renew',display_order=215,is_sensitive=0 WHERE permission_code='LIC.LICENSE.RENEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('LIC.LICENSE.ACTIVATE','License - Activate','LICENSING','LICENSE','ACTIVATE','الترخيص - تفعيل','License - Activate','صلاحية تشغيلية ضمن قسم الترخيص',216,0);
UPDATE permissions SET group_code='LICENSING',feature_code='LICENSE',operation_code='ACTIVATE',name_ar='الترخيص - تفعيل',name_en='License - Activate',display_order=216,is_sensitive=0 WHERE permission_code='LIC.LICENSE.ACTIVATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('LIC.DEVICE.VIEW','Device - View','LICENSING','DEVICE','VIEW','الأجهزة - عرض','Device - View','صلاحية تشغيلية ضمن قسم الترخيص',217,0);
UPDATE permissions SET group_code='LICENSING',feature_code='DEVICE',operation_code='VIEW',name_ar='الأجهزة - عرض',name_en='Device - View',display_order=217,is_sensitive=0 WHERE permission_code='LIC.DEVICE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('LIC.DEVICE.MANAGE','Device - Manage','LICENSING','DEVICE','MANAGE','الأجهزة - إدارة','Device - Manage','صلاحية تشغيلية ضمن قسم الترخيص',218,1);
UPDATE permissions SET group_code='LICENSING',feature_code='DEVICE',operation_code='MANAGE',name_ar='الأجهزة - إدارة',name_en='Device - Manage',display_order=218,is_sensitive=1 WHERE permission_code='LIC.DEVICE.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('UPDATE.CHECK','Check - View','UPDATES','CHECK','VIEW','Check - عرض','Check - View','صلاحية تشغيلية ضمن قسم التحديثات',219,0);
UPDATE permissions SET group_code='UPDATES',feature_code='CHECK',operation_code='VIEW',name_ar='Check - عرض',name_en='Check - View',display_order=219,is_sensitive=0 WHERE permission_code='UPDATE.CHECK';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('UPDATE.INSTALL','Install - View','UPDATES','INSTALL','VIEW','Install - عرض','Install - View','صلاحية تشغيلية ضمن قسم التحديثات',220,0);
UPDATE permissions SET group_code='UPDATES',feature_code='INSTALL',operation_code='VIEW',name_ar='Install - عرض',name_en='Install - View',display_order=220,is_sensitive=0 WHERE permission_code='UPDATE.INSTALL';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('UPDATE.MANUAL_INSTALL','Manual Install - View','UPDATES','MANUAL_INSTALL','VIEW','Manual Install - عرض','Manual Install - View','صلاحية تشغيلية ضمن قسم التحديثات',221,0);
UPDATE permissions SET group_code='UPDATES',feature_code='MANUAL_INSTALL',operation_code='VIEW',name_ar='Manual Install - عرض',name_en='Manual Install - View',display_order=221,is_sensitive=0 WHERE permission_code='UPDATE.MANUAL_INSTALL';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AI.USE','Use - View','AI','USE','VIEW','Use - عرض','Use - View','صلاحية تشغيلية ضمن قسم الذكاء الاصطناعي',222,0);
UPDATE permissions SET group_code='AI',feature_code='USE',operation_code='VIEW',name_ar='Use - عرض',name_en='Use - View',display_order=222,is_sensitive=0 WHERE permission_code='AI.USE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HELP.VIEW','View - View','HELP','VIEW','VIEW','View - عرض','View - View','صلاحية تشغيلية ضمن قسم المساعدة',223,0);
UPDATE permissions SET group_code='HELP',feature_code='VIEW',operation_code='VIEW',name_ar='View - عرض',name_en='View - View',display_order=223,is_sensitive=0 WHERE permission_code='HELP.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HELP.SUPPORT','Support - View','HELP','SUPPORT','VIEW','Support - عرض','Support - View','صلاحية تشغيلية ضمن قسم المساعدة',224,0);
UPDATE permissions SET group_code='HELP',feature_code='SUPPORT',operation_code='VIEW',name_ar='Support - عرض',name_en='Support - View',display_order=224,is_sensitive=0 WHERE permission_code='HELP.SUPPORT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HELP.SUGGESTIONS','Suggestions - View','HELP','SUGGESTIONS','VIEW','Suggestions - عرض','Suggestions - View','صلاحية تشغيلية ضمن قسم المساعدة',225,0);
UPDATE permissions SET group_code='HELP',feature_code='SUGGESTIONS',operation_code='VIEW',name_ar='Suggestions - عرض',name_en='Suggestions - View',display_order=225,is_sensitive=0 WHERE permission_code='HELP.SUGGESTIONS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HELP.ABOUT','About - View','HELP','ABOUT','VIEW','About - عرض','About - View','صلاحية تشغيلية ضمن قسم المساعدة',226,0);
UPDATE permissions SET group_code='HELP',feature_code='ABOUT',operation_code='VIEW',name_ar='About - عرض',name_en='About - View',display_order=226,is_sensitive=0 WHERE permission_code='HELP.ABOUT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.FISCAL.ROLL_FORWARD','Fiscal - Roll Forward','ACCOUNTING','FISCAL','ROLL_FORWARD','Fiscal - تدوير السنة المالية','Fiscal - Roll Forward','صلاحية تشغيلية ضمن قسم المحاسبة',227,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='FISCAL',operation_code='ROLL_FORWARD',name_ar='Fiscal - تدوير السنة المالية',name_en='Fiscal - Roll Forward',display_order=227,is_sensitive=1 WHERE permission_code='ACC.FISCAL.ROLL_FORWARD';
DELETE FROM role_permissions WHERE role_id IN (SELECT role_id FROM roles WHERE role_code='ROLE_USER');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_USER' AND p.is_active=1 AND p.permission_code NOT IN ('ACC.FISCAL.ROLL_FORWARD','COMPANY_CREATE','COMPANY_EDIT','COMPANY_DELETE','USER_VIEW','USER_CREATE','USER_EDIT','USER_DELETE','ROLE_VIEW','ROLE_EDIT','PERMISSION_VIEW','ORG.COMPANY.CREATE','ORG.COMPANY.DELETE','ORG.COMPANY.UPDATE','SYS.ADMIN','SYS.PERMISSIONS.VIEW','SYS.PERMISSIONS.MANAGE','SYS.PERMISSION_TEMPLATES.MANAGE','SYS.ROLES.CREATE','SYS.ROLES.DELETE','SYS.ROLES.UPDATE','SYS.ROLES.VIEW','SYS.USERS.CREATE','SYS.USERS.DELETE','SYS.USERS.DISABLE','SYS.USERS.UPDATE','SYS.USERS.VIEW');
-- Viewer remains read-only: only explicit VIEW permissions.
DELETE FROM role_permissions WHERE role_id IN (SELECT role_id FROM roles WHERE role_code='ROLE_VIEWER');
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code='ROLE_VIEWER' AND p.is_active=1 AND (p.operation_code='VIEW' OR p.permission_code IN ('SYS.SETTINGS.VIEW','SYS.USERS.VIEW','SYS.ROLES.VIEW','SYS.PERMISSIONS.VIEW','REPORT.VIEW','AUDIT.VIEW','SYSTEM.VIEW'));
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.PERMISSIONS.MANAGE','Permission Management','SYSTEM','PERMISSIONS','MANAGE','إدارة الصلاحيات','Permission Management','إدارة صلاحيات المستخدمين',228,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='PERMISSIONS',operation_code='MANAGE',name_ar='إدارة الصلاحيات',name_en='Permission Management',display_order=228,is_sensitive=1 WHERE permission_code='SYS.PERMISSIONS.MANAGE';

-- Administrator receives every active permission.
INSERT OR IGNORE INTO role_permissions(role_id,permission_id)
SELECT r.role_id,p.permission_id FROM roles r CROSS JOIN permissions p WHERE r.role_code='ROLE_ADMINISTRATOR' AND p.is_active=1;

-- @shoutech-migration 006_outbound_messages.sql 506299A82CF5D5EAF6BE77692E1DAC496AB1D63A0A5BDEC523E86451E19B2E4D C0C89833B23FD881AE3EA4D75E44CBE0FE64F921F10495A409DE437D744E1CA7 C0C89833B23FD881AE3EA4D75E44CBE0FE64F921F10495A409DE437D744E1CA7 C0C89833B23FD881AE3EA4D75E44CBE0FE64F921F10495A409DE437D744E1CA7
CREATE TABLE IF NOT EXISTS outbound_messages (
    outbound_message_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT,
    channel TEXT NOT NULL CHECK (channel IN ('WEBHOOK', 'EMAIL', 'SMS')),
    recipient_address TEXT NOT NULL,
    subject TEXT,
    body TEXT NOT NULL,
    body_format TEXT NOT NULL DEFAULT 'TEXT',
    status TEXT NOT NULL DEFAULT 'QUEUED' CHECK (status IN ('QUEUED', 'SENDING', 'SENT', 'FAILED')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    max_attempts INTEGER NOT NULL DEFAULT 6 CHECK (max_attempts > 0),
    next_attempt_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    sent_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_outbound_messages_due
    ON outbound_messages(status, next_attempt_at, outbound_message_id);
-- @shoutech-migration 011_deletion_audit.sql FE084BC41EE3D18A8026ADC2120DDEE47B2D30E2DC3E5024AD2794CDEA9B3C30 3C715A2063E526892B2DA6093128FE68A5DB8F3B271AE813B654F0B9879FAACF 3C715A2063E526892B2DA6093128FE68A5DB8F3B271AE813B654F0B9879FAACF 3C715A2063E526892B2DA6093128FE68A5DB8F3B271AE813B654F0B9879FAACF
-- Delete audit storage is DBA-owned; service code only records audit events.
CREATE TABLE IF NOT EXISTS deletion_audit_log (
    event_id TEXT PRIMARY KEY,
    company_code TEXT NOT NULL,
    table_name TEXT NOT NULL,
    record_key TEXT NOT NULL,
    action_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    method TEXT NOT NULL,
    reason TEXT,
    record_snapshot TEXT
);
CREATE INDEX IF NOT EXISTS ix_deletion_audit_company_time
    ON deletion_audit_log(company_code, occurred_at);
-- @shoutech-migration 015_permission_extensions.sql 42BBEAFC78569F6CE6FC5B45D3B1E6908A93FDDCF38C41BD0270F7D1C65B67CC 0853DA2CE99FD141E9ECAFAF3B852170480012E6453123568154A7125E874E83 0853DA2CE99FD141E9ECAFAF3B852170480012E6453123568154A7125E874E83 0853DA2CE99FD141E9ECAFAF3B852170480012E6453123568154A7125E874E83
-- Permission extension columns formerly installed by Data.cs.
-- Conditional directives make the SQLite migration safe for existing company databases.
-- @if-column-missing permissions.group_code
ALTER TABLE permissions ADD COLUMN group_code TEXT NOT NULL DEFAULT 'SYSTEM';
-- @if-column-missing permissions.feature_code
ALTER TABLE permissions ADD COLUMN feature_code TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.operation_code
ALTER TABLE permissions ADD COLUMN operation_code TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.name_ar
ALTER TABLE permissions ADD COLUMN name_ar TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.name_en
ALTER TABLE permissions ADD COLUMN name_en TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.description
ALTER TABLE permissions ADD COLUMN description TEXT;
-- @if-column-missing permissions.display_order
ALTER TABLE permissions ADD COLUMN display_order INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing permissions.is_sensitive
ALTER TABLE permissions ADD COLUMN is_sensitive INTEGER NOT NULL DEFAULT 0;
-- @shoutech-migration 028_permissions_phase3.sql BE4BC3C2E2D6D03FB2F7B4C912747A23C35C54EF161C34CE1D0080002D1C1869 69DC49518EED4CC6FD22800AC37C5F860052EEFBFFDA21442A7B5B218E7B4952 69DC49518EED4CC6FD22800AC37C5F860052EEFBFFDA21442A7B5B218E7B4952 69DC49518EED4CC6FD22800AC37C5F860052EEFBFFDA21442A7B5B218E7B4952
-- Phase 3 permissions: Payment Reconciliation, Projects, Fixed Assets.
-- Insert-or-update follows the 004_permissions.sql pattern; grants follow 003_roles.sql.

INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.PAYREC.VIEW','Payment Reconciliation - View','ACCOUNTING','PAYREC','VIEW','تسوية المدفوعات - عرض','Payment Reconciliation - View','Operational permission (Phase 3)',300,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='PAYREC',operation_code='VIEW',name_ar='تسوية المدفوعات - عرض',name_en='Payment Reconciliation - View',display_order=300,is_sensitive=0 WHERE permission_code='ACC.PAYREC.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.PAYREC.ALLOCATE','Payment Reconciliation - Allocate','ACCOUNTING','PAYREC','ALLOCATE','تسوية المدفوعات - تخصيص','Payment Reconciliation - Allocate','Operational permission (Phase 3)',301,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='PAYREC',operation_code='ALLOCATE',name_ar='تسوية المدفوعات - تخصيص',name_en='Payment Reconciliation - Allocate',display_order=301,is_sensitive=1 WHERE permission_code='ACC.PAYREC.ALLOCATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PRJ.PROJECT.VIEW','Projects - View','PROJECTS','PROJECT','VIEW','المشاريع - عرض','Projects - View','Operational permission (Phase 3)',302,0);
UPDATE permissions SET group_code='PROJECTS',feature_code='PROJECT',operation_code='VIEW',name_ar='المشاريع - عرض',name_en='Projects - View',display_order=302,is_sensitive=0 WHERE permission_code='PRJ.PROJECT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PRJ.PROJECT.CREATE','Projects - Create','PROJECTS','PROJECT','CREATE','المشاريع - إنشاء','Projects - Create','Operational permission (Phase 3)',303,0);
UPDATE permissions SET group_code='PROJECTS',feature_code='PROJECT',operation_code='CREATE',name_ar='المشاريع - إنشاء',name_en='Projects - Create',display_order=303,is_sensitive=0 WHERE permission_code='PRJ.PROJECT.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PRJ.PROJECT.UPDATE','Projects - Update','PROJECTS','PROJECT','UPDATE','المشاريع - تعديل','Projects - Update','Operational permission (Phase 3)',304,0);
UPDATE permissions SET group_code='PROJECTS',feature_code='PROJECT',operation_code='UPDATE',name_ar='المشاريع - تعديل',name_en='Projects - Update',display_order=304,is_sensitive=0 WHERE permission_code='PRJ.PROJECT.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PRJ.PROJECT.DELETE','Projects - Delete','PROJECTS','PROJECT','DELETE','المشاريع - حذف','Projects - Delete','Operational permission (Phase 3)',305,1);
UPDATE permissions SET group_code='PROJECTS',feature_code='PROJECT',operation_code='DELETE',name_ar='المشاريع - حذف',name_en='Projects - Delete',display_order=305,is_sensitive=1 WHERE permission_code='PRJ.PROJECT.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PRJ.TASK.MANAGE','Project Tasks - Manage','PROJECTS','TASK','MANAGE','مهام المشاريع - إدارة','Project Tasks - Manage','Operational permission (Phase 3)',306,0);
UPDATE permissions SET group_code='PROJECTS',feature_code='TASK',operation_code='MANAGE',name_ar='مهام المشاريع - إدارة',name_en='Project Tasks - Manage',display_order=306,is_sensitive=0 WHERE permission_code='PRJ.TASK.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AST.ASSET.VIEW','Fixed Assets - View','ASSETS','ASSET','VIEW','الأصول الثابتة - عرض','Fixed Assets - View','Operational permission (Phase 3)',307,0);
UPDATE permissions SET group_code='ASSETS',feature_code='ASSET',operation_code='VIEW',name_ar='الأصول الثابتة - عرض',name_en='Fixed Assets - View',display_order=307,is_sensitive=0 WHERE permission_code='AST.ASSET.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AST.ASSET.CREATE','Fixed Assets - Create','ASSETS','ASSET','CREATE','الأصول الثابتة - إنشاء','Fixed Assets - Create','Operational permission (Phase 3)',308,0);
UPDATE permissions SET group_code='ASSETS',feature_code='ASSET',operation_code='CREATE',name_ar='الأصول الثابتة - إنشاء',name_en='Fixed Assets - Create',display_order=308,is_sensitive=0 WHERE permission_code='AST.ASSET.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AST.ASSET.UPDATE','Fixed Assets - Update','ASSETS','ASSET','UPDATE','الأصول الثابتة - تعديل','Fixed Assets - Update','Operational permission (Phase 3)',309,0);
UPDATE permissions SET group_code='ASSETS',feature_code='ASSET',operation_code='UPDATE',name_ar='الأصول الثابتة - تعديل',name_en='Fixed Assets - Update',display_order=309,is_sensitive=0 WHERE permission_code='AST.ASSET.UPDATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('AST.ASSET.DELETE','Fixed Assets - Delete','ASSETS','ASSET','DELETE','الأصول الثابتة - حذف','Fixed Assets - Delete','Operational permission (Phase 3)',310,1);
UPDATE permissions SET group_code='ASSETS',feature_code='ASSET',operation_code='DELETE',name_ar='الأصول الثابتة - حذف',name_en='Fixed Assets - Delete',display_order=310,is_sensitive=1 WHERE permission_code='AST.ASSET.DELETE';

-- Administrator: all Phase 3 permissions. User: all except deletes. Viewer: view only.
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.PAYREC.VIEW','ACC.PAYREC.ALLOCATE','PRJ.PROJECT.VIEW','PRJ.PROJECT.CREATE','PRJ.PROJECT.UPDATE','PRJ.PROJECT.DELETE','PRJ.TASK.MANAGE','AST.ASSET.VIEW','AST.ASSET.CREATE','AST.ASSET.UPDATE','AST.ASSET.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.PAYREC.VIEW','ACC.PAYREC.ALLOCATE','PRJ.PROJECT.VIEW','PRJ.PROJECT.CREATE','PRJ.PROJECT.UPDATE','PRJ.TASK.MANAGE','AST.ASSET.VIEW','AST.ASSET.CREATE','AST.ASSET.UPDATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.PAYREC.VIEW','PRJ.PROJECT.VIEW','AST.ASSET.VIEW');
-- @shoutech-migration 029_security_hardening.sql 6E385DAB1ECF3162956EA9F8A59ADA1A53C3250F3D92135F74DC23CC73AD7D2C B6AB189F3B7371BFB8FF1B337584B7FD52B92F1D43C91DAB34E1B6EED37A365B B6AB189F3B7371BFB8FF1B337584B7FD52B92F1D43C91DAB34E1B6EED37A365B B6AB189F3B7371BFB8FF1B337584B7FD52B92F1D43C91DAB34E1B6EED37A365B
-- Phase 4 enterprise security hardening (SQLite).
-- 1) Login lockout state on users: failed-attempt counter + lock expiry.
--    Enforced by AuthServ using SecuritySettings (MaxLoginAttempts / LockoutDurationMinutes).
ALTER TABLE users ADD COLUMN failed_login_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE users ADD COLUMN locked_until TEXT NULL;

-- 2) Durable security audit trail for authentication and data-governance events
--    (login success/failure, account lockout, data export). Written by
--    SecurityAuditLog (cod/app) on a best-effort basis; never stores passwords.
CREATE TABLE IF NOT EXISTS security_audit_log (
    security_audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NULL,
    user_id TEXT NULL,
    username TEXT NOT NULL,
    event_type TEXT NOT NULL,
    details TEXT NULL,
    occurred_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_security_audit_username_time
    ON security_audit_log(username, occurred_at);
CREATE INDEX IF NOT EXISTS ix_security_audit_event_time
    ON security_audit_log(event_type, occurred_at);
CREATE INDEX IF NOT EXISTS ix_security_audit_company_time
    ON security_audit_log(company_id, occurred_at);

-- 3) Missing indexes on high-traffic transactional tables (company + date/status
--    lookups used by reconciliation, aging and list windows; account statements
--    filter journal lines by account; document reversal lookups by original doc).
CREATE INDEX IF NOT EXISTS ix_sales_documents_company_date
    ON sales_documents(company_id, doc_date);
CREATE INDEX IF NOT EXISTS ix_sales_documents_company_status
    ON sales_documents(company_id, status);
CREATE INDEX IF NOT EXISTS ix_purchase_documents_company_date
    ON purchase_documents(company_id, doc_date);
CREATE INDEX IF NOT EXISTS ix_purchase_documents_company_status
    ON purchase_documents(company_id, status);
CREATE INDEX IF NOT EXISTS ix_journal_entry_lines_account
    ON journal_entry_lines(account_id);
CREATE INDEX IF NOT EXISTS ix_document_reversals_company_original
    ON document_reversals(company_id, original_doc_kind, original_doc_id);
-- @shoutech-migration 036_definitions.sql E34BB38F8112F5DFEF531D31432568C2197E8704DF9C978DFC9211BCDFF2D7AB 8FE26B698ED548CBCE9FA4D7C98357B798E1C32A66CC945D6D660BC3727C2031 8FE26B698ED548CBCE9FA4D7C98357B798E1C32A66CC945D6D660BC3727C2031 8FE26B698ED548CBCE9FA4D7C98357B798E1C32A66CC945D6D660BC3727C2031
-- Migration 036 (SQLite): Definitions framework tables (ribbon completion F1).
-- Owns: doc-type definitions, payment methods (+ global seed rows), POS
-- terminals, areas, targets, discount rules, customer prices, loyalty
-- accounts/entries, handheld devices, device routes, drivers, delivery
-- prices/requests, driver settlements, custom fields (+values),
-- installments (+schedule). Dates are ISO text (yyyy-MM-dd) on both
-- providers. Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS doc_type_definitions (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    set_kind TEXT NOT NULL CHECK (set_kind IN ('VOUCHER','SECURITY','PAYMENT','INVOICE','ORDER')),
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    gl_account_code TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_doc_type_definitions_company_kind_code
    ON doc_type_definitions(company_id, set_kind, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS payment_methods (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    kind TEXT NOT NULL CHECK (kind IN ('Cash','BankTransfer','Cheque','Card','Other')),
    gl_account_code TEXT,
    settlement_bank_code TEXT,
    commission_pct REAL NOT NULL DEFAULT 0 CHECK (commission_pct >= 0 AND commission_pct <= 100),
    commission_account_code TEXT,
    requires_reference INTEGER NOT NULL DEFAULT 0,
    is_pos_enabled INTEGER NOT NULL DEFAULT 0,
    pos_sort_order INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_methods_company_code
    ON payment_methods(company_id, code) WHERE is_deleted = 0;

-- Global default payment methods (company_id '' = shared defaults; a company
-- saving one of these creates its own override row).
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-cash', '', 'CASH', 'Cash', NULL, 'Cash', 1, 1, 'seed');
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-bank', '', 'BANK', 'Bank Transfer', NULL, 'BankTransfer', 0, 2, 'seed');
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-cheque', '', 'CHEQUE', 'Cheque', NULL, 'Cheque', 0, 3, 'seed');
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-visa', '', 'VISA', 'Visa', NULL, 'Card', 1, 4, 'seed');
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-mc', '', 'MC', 'Mastercard', NULL, 'Card', 1, 5, 'seed');
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-mada', '', 'MADA', 'mada', NULL, 'Card', 1, 6, 'seed');
INSERT OR IGNORE INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-amex', '', 'AMEX', 'American Express', NULL, 'Card', 1, 7, 'seed');

CREATE TABLE IF NOT EXISTS pos_terminals (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    warehouse_code TEXT,
    cash_account_code TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_terminals_company_code
    ON pos_terminals(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS areas (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    parent_id TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_areas_company_code
    ON areas(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS targets (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    owner_kind TEXT NOT NULL CHECK (owner_kind IN ('PARTY','REP')),
    owner_code TEXT NOT NULL,
    period_kind TEXT NOT NULL CHECK (period_kind IN ('M','Q','Y')),
    period_start TEXT NOT NULL,
    target_amount REAL NOT NULL CHECK (target_amount >= 0),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_targets_company_owner
    ON targets(company_id, owner_kind, owner_code);

CREATE TABLE IF NOT EXISTS discount_rules (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    scope TEXT NOT NULL CHECK (scope IN ('MATERIAL','CUSTOMER','POS')),
    target_code TEXT NOT NULL,
    discount_pct REAL NOT NULL CHECK (discount_pct >= 0 AND discount_pct <= 100),
    min_qty REAL NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_discount_rules_company_scope
    ON discount_rules(company_id, scope, target_code);

CREATE TABLE IF NOT EXISTS customer_prices (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_code TEXT NOT NULL,
    material_code TEXT NOT NULL,
    price REAL NOT NULL CHECK (price >= 0),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_customer_prices_company_party_material
    ON customer_prices(company_id, party_code, material_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS loyalty_accounts (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_code TEXT NOT NULL,
    points_balance REAL NOT NULL DEFAULT 0,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_loyalty_accounts_company_party
    ON loyalty_accounts(company_id, party_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS loyalty_entries (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_id TEXT NOT NULL REFERENCES loyalty_accounts(id),
    entry_date TEXT NOT NULL,
    points REAL NOT NULL,
    doc_ref TEXT,
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_loyalty_entries_account
    ON loyalty_entries(account_id);

CREATE TABLE IF NOT EXISTS handheld_devices (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    rep_code TEXT,
    device_serial TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_handheld_devices_company_code
    ON handheld_devices(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS device_routes (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    device_code TEXT NOT NULL,
    rep_code TEXT,
    area_code TEXT,
    weekday INTEGER NOT NULL CHECK (weekday >= 0 AND weekday <= 6),
    visit_order INTEGER NOT NULL DEFAULT 0,
    party_code TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_device_routes_company_device
    ON device_routes(company_id, device_code);

CREATE TABLE IF NOT EXISTS drivers (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    phone TEXT,
    vehicle_no TEXT,
    license_no TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_drivers_company_code
    ON drivers(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS delivery_prices (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    area_code TEXT NOT NULL,
    price REAL NOT NULL CHECK (price >= 0),
    min_order REAL NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_prices_company_area
    ON delivery_prices(company_id, area_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS delivery_requests (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    request_date TEXT NOT NULL,
    party_code TEXT,
    address TEXT,
    area_code TEXT,
    driver_code TEXT,
    status TEXT NOT NULL DEFAULT 'Pending',
    amount REAL NOT NULL DEFAULT 0,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_requests_company_docno
    ON delivery_requests(company_id, doc_no) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS driver_settlements (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    driver_code TEXT NOT NULL,
    settle_date TEXT NOT NULL,
    collected_amount REAL NOT NULL DEFAULT 0,
    fees_amount REAL NOT NULL DEFAULT 0,
    net_amount REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open',
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_driver_settlements_company_driver
    ON driver_settlements(company_id, driver_code);

CREATE TABLE IF NOT EXISTS custom_field_definitions (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    field_code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    field_type TEXT NOT NULL CHECK (field_type IN ('TEXT','NUMBER','DATE','BOOL')),
    is_required INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_custom_field_definitions_company_entity_field
    ON custom_field_definitions(company_id, entity_kind, field_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS custom_field_values (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    entity_code TEXT NOT NULL,
    field_code TEXT NOT NULL,
    value_text TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_custom_field_values_company_entity_field
    ON custom_field_values(company_id, entity_kind, entity_code, field_code);

CREATE TABLE IF NOT EXISTS installments (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_code TEXT NOT NULL,
    doc_ref TEXT,
    total_amount REAL NOT NULL CHECK (total_amount > 0),
    down_payment REAL NOT NULL DEFAULT 0 CHECK (down_payment >= 0),
    installment_count INTEGER NOT NULL CHECK (installment_count > 0),
    start_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active',
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_installments_company_party
    ON installments(company_id, party_code);

CREATE TABLE IF NOT EXISTS installment_schedule (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    installment_id TEXT NOT NULL REFERENCES installments(id),
    seq INTEGER NOT NULL,
    due_date TEXT NOT NULL,
    amount REAL NOT NULL CHECK (amount >= 0),
    paid_amount REAL NOT NULL DEFAULT 0,
    paid_date TEXT,
    status TEXT NOT NULL DEFAULT 'Due',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_installment_schedule_installment
    ON installment_schedule(installment_id);
-- @shoutech-migration 038_permissions_ribbon.sql DC4D063A81EC813009F0F630603C9609CAF7DE6FC2FA31021C5E17EA795699B8 6E5D4641919AD1B4824D5B5BF930B198EC125DD16BF8D9E79296098379F32327 6E5D4641919AD1B4824D5B5BF930B198EC125DD16BF8D9E79296098379F32327 6E5D4641919AD1B4824D5B5BF930B198EC125DD16BF8D9E79296098379F32327
-- Ribbon completion permissions: order conversion, custom fields, and the generic
-- ACC/INV/PURCH/SALES/SYS codes referenced by PermCat but never seeded.
-- Insert-or-update follows the 004/028 pattern. name_ar carries the English name
-- per the owner's English-only rule (Arabic lives only in the Ar.json catalog).
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.CREATE','Accounts - Create','ACCOUNTING','ACC','CREATE','Accounts - Create','Accounts - Create','Operational permission (ribbon completion)',320,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACC',operation_code='CREATE',name_ar='Accounts - Create',name_en='Accounts - Create',display_order=320,is_sensitive=0 WHERE permission_code='ACC.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.DELETE','Accounts - Delete','ACCOUNTING','ACC','DELETE','Accounts - Delete','Accounts - Delete','Operational permission (ribbon completion)',321,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACC',operation_code='DELETE',name_ar='Accounts - Delete',name_en='Accounts - Delete',display_order=321,is_sensitive=1 WHERE permission_code='ACC.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.POST','Accounts - Post','ACCOUNTING','ACC','POST','Accounts - Post','Accounts - Post','Operational permission (ribbon completion)',322,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACC',operation_code='POST',name_ar='Accounts - Post',name_en='Accounts - Post',display_order=322,is_sensitive=0 WHERE permission_code='ACC.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.VIEW','Accounts - View','ACCOUNTING','ACC','VIEW','Accounts - View','Accounts - View','Operational permission (ribbon completion)',323,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='ACC',operation_code='VIEW',name_ar='Accounts - View',name_en='Accounts - View',display_order=323,is_sensitive=0 WHERE permission_code='ACC.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.CREATE','Inventory - Create','INVENTORY','INV','CREATE','Inventory - Create','Inventory - Create','Operational permission (ribbon completion)',324,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='INV',operation_code='CREATE',name_ar='Inventory - Create',name_en='Inventory - Create',display_order=324,is_sensitive=0 WHERE permission_code='INV.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.DELETE','Inventory - Delete','INVENTORY','INV','DELETE','Inventory - Delete','Inventory - Delete','Operational permission (ribbon completion)',325,1);
UPDATE permissions SET group_code='INVENTORY',feature_code='INV',operation_code='DELETE',name_ar='Inventory - Delete',name_en='Inventory - Delete',display_order=325,is_sensitive=1 WHERE permission_code='INV.DELETE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.MOVE','Inventory - Move','INVENTORY','INV','MOVE','Inventory - Move','Inventory - Move','Operational permission (ribbon completion)',326,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='INV',operation_code='MOVE',name_ar='Inventory - Move',name_en='Inventory - Move',display_order=326,is_sensitive=0 WHERE permission_code='INV.MOVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('INV.VIEW','Inventory - View','INVENTORY','INV','VIEW','Inventory - View','Inventory - View','Operational permission (ribbon completion)',327,0);
UPDATE permissions SET group_code='INVENTORY',feature_code='INV',operation_code='VIEW',name_ar='Inventory - View',name_en='Inventory - View',display_order=327,is_sensitive=0 WHERE permission_code='INV.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.CREATE','Purchases - Create','PURCHASES','PURCH','CREATE','Purchases - Create','Purchases - Create','Operational permission (ribbon completion)',328,0);
UPDATE permissions SET group_code='PURCHASES',feature_code='PURCH',operation_code='CREATE',name_ar='Purchases - Create',name_en='Purchases - Create',display_order=328,is_sensitive=0 WHERE permission_code='PURCH.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.ORDER.CONVERT','Purchase Orders - Convert to Invoice','PURCHASES','ORDER','CONVERT','Purchase Orders - Convert to Invoice','Purchase Orders - Convert to Invoice','Operational permission (ribbon completion)',329,1);
UPDATE permissions SET group_code='PURCHASES',feature_code='ORDER',operation_code='CONVERT',name_ar='Purchase Orders - Convert to Invoice',name_en='Purchase Orders - Convert to Invoice',display_order=329,is_sensitive=1 WHERE permission_code='PURCH.ORDER.CONVERT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.POST','Purchases - Post','PURCHASES','PURCH','POST','Purchases - Post','Purchases - Post','Operational permission (ribbon completion)',330,0);
UPDATE permissions SET group_code='PURCHASES',feature_code='PURCH',operation_code='POST',name_ar='Purchases - Post',name_en='Purchases - Post',display_order=330,is_sensitive=0 WHERE permission_code='PURCH.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('PURCH.VIEW','Purchases - View','PURCHASES','PURCH','VIEW','Purchases - View','Purchases - View','Operational permission (ribbon completion)',331,0);
UPDATE permissions SET group_code='PURCHASES',feature_code='PURCH',operation_code='VIEW',name_ar='Purchases - View',name_en='Purchases - View',display_order=331,is_sensitive=0 WHERE permission_code='PURCH.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.CREATE','Sales - Create','SALES','SALES','CREATE','Sales - Create','Sales - Create','Operational permission (ribbon completion)',332,0);
UPDATE permissions SET group_code='SALES',feature_code='SALES',operation_code='CREATE',name_ar='Sales - Create',name_en='Sales - Create',display_order=332,is_sensitive=0 WHERE permission_code='SALES.CREATE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.CONVERT','Sales Orders - Convert to Invoice','SALES','ORDER','CONVERT','Sales Orders - Convert to Invoice','Sales Orders - Convert to Invoice','Operational permission (ribbon completion)',333,1);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='CONVERT',name_ar='Sales Orders - Convert to Invoice',name_en='Sales Orders - Convert to Invoice',display_order=333,is_sensitive=1 WHERE permission_code='SALES.ORDER.CONVERT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.POST','Sales - Post','SALES','SALES','POST','Sales - Post','Sales - Post','Operational permission (ribbon completion)',334,0);
UPDATE permissions SET group_code='SALES',feature_code='SALES',operation_code='POST',name_ar='Sales - Post',name_en='Sales - Post',display_order=334,is_sensitive=0 WHERE permission_code='SALES.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.VIEW','Sales - View','SALES','SALES','VIEW','Sales - View','Sales - View','Operational permission (ribbon completion)',335,0);
UPDATE permissions SET group_code='SALES',feature_code='SALES',operation_code='VIEW',name_ar='Sales - View',name_en='Sales - View',display_order=335,is_sensitive=0 WHERE permission_code='SALES.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.BACKUP','System - Backup','SYSTEM','SYS','BACKUP','System - Backup','System - Backup','Operational permission (ribbon completion)',336,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='SYS',operation_code='BACKUP',name_ar='System - Backup',name_en='System - Backup',display_order=336,is_sensitive=1 WHERE permission_code='SYS.BACKUP';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.CUSTOM_FIELDS.MANAGE','System - Manage Custom Fields','SYSTEM','CUSTOM_FIELDS','MANAGE','System - Manage Custom Fields','System - Manage Custom Fields','Operational permission (ribbon completion)',337,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='CUSTOM_FIELDS',operation_code='MANAGE',name_ar='System - Manage Custom Fields',name_en='System - Manage Custom Fields',display_order=337,is_sensitive=1 WHERE permission_code='SYS.CUSTOM_FIELDS.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.SETTINGS','System - Settings','SYSTEM','SYS','SETTINGS','System - Settings','System - Settings','Operational permission (ribbon completion)',338,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='SYS',operation_code='SETTINGS',name_ar='System - Settings',name_en='System - Settings',display_order=338,is_sensitive=1 WHERE permission_code='SYS.SETTINGS';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SYS.USERS','System - Users','SYSTEM','SYS','USERS','System - Users','System - Users','Operational permission (ribbon completion)',339,1);
UPDATE permissions SET group_code='SYSTEM',feature_code='SYS',operation_code='USERS',name_ar='System - Users',name_en='System - Users',display_order=339,is_sensitive=1 WHERE permission_code='SYS.USERS';

-- Administrator: all. User: all except deletes. Viewer: view only.
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.CREATE','ACC.DELETE','ACC.POST','ACC.VIEW','INV.CREATE','INV.DELETE','INV.MOVE','INV.VIEW','PURCH.CREATE','PURCH.ORDER.CONVERT','PURCH.POST','PURCH.VIEW','SALES.CREATE','SALES.ORDER.CONVERT','SALES.POST','SALES.VIEW','SYS.BACKUP','SYS.CUSTOM_FIELDS.MANAGE','SYS.SETTINGS','SYS.USERS');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.CREATE','ACC.POST','ACC.VIEW','INV.CREATE','INV.MOVE','INV.VIEW','PURCH.CREATE','PURCH.ORDER.CONVERT','PURCH.POST','PURCH.VIEW','SALES.CREATE','SALES.ORDER.CONVERT','SALES.POST','SALES.VIEW','SYS.BACKUP','SYS.CUSTOM_FIELDS.MANAGE','SYS.SETTINGS','SYS.USERS');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.VIEW','INV.VIEW','PURCH.VIEW','SALES.VIEW');
-- @shoutech-migration 051_companies_profile_columns.sql E5E4594ABC1658EC0F8E93D904209E689C3E8AF5265B426C91544F7611FAF9A0 13DBED268E9C9C64EBBC7556040ABF95FE84B01501FA8392FABBB9E11B50A701 13DBED268E9C9C64EBBC7556040ABF95FE84B01501FA8392FABBB9E11B50A701 13DBED268E9C9C64EBBC7556040ABF95FE84B01501FA8392FABBB9E11B50A701
-- Migration 051 (SQLite): companies dual-store remediation (persistence
-- audit section 9.1). The company profile round-tripped ~30 fields through
-- companies.json while the database companies table (migration 001) had
-- only a handful of writable columns: TaxNumber had NO column at all (the
-- wizard wrote License into commercial_registration while the company-edit
-- path wrote Tax-first into the same column, so the two paths disagreed),
-- and Website/AddressLine2/City/Country/Notes/DecimalPlaces/CalendarType/
-- NumberGrouping/CoaTemplateCode had no columns either. This migration adds
-- the missing columns so every profile field maps to its own column
-- (License -> commercial_registration, TaxNumber -> tax_number).
-- Existing rows keep commercial_registration as-is: values written by the
-- create path are license numbers; a value written by the old edit path
-- cannot be distinguished reliably, so no backfill is attempted.
-- Runtime services must not create tables or columns; all DDL lives here
-- and in the PostgreSQL counterpart.

ALTER TABLE companies ADD COLUMN tax_number TEXT;
ALTER TABLE companies ADD COLUMN website TEXT;
ALTER TABLE companies ADD COLUMN address_line2 TEXT;
ALTER TABLE companies ADD COLUMN city TEXT;
ALTER TABLE companies ADD COLUMN country TEXT;
ALTER TABLE companies ADD COLUMN notes TEXT;
ALTER TABLE companies ADD COLUMN decimal_places INTEGER NOT NULL DEFAULT 2;
ALTER TABLE companies ADD COLUMN calendar_type TEXT NOT NULL DEFAULT 'Gregorian';
ALTER TABLE companies ADD COLUMN number_grouping TEXT NOT NULL DEFAULT '3-3-3';
ALTER TABLE companies ADD COLUMN coa_template_code TEXT NOT NULL DEFAULT 'SIMPLE';
-- @shoutech-migration 115_report_designer.sql 16A8E55F08047EE9E36B5677E77C5A87B77D9B14A6B330F9EFF94DDF1D0DB2E4 D47FBFF868CCE57977E8D5978B3678320BD0C04CA4A6C3FCB0A15250F006B2FF D47FBFF868CCE57977E8D5978B3678320BD0C04CA4A6C3FCB0A15250F006B2FF D47FBFF868CCE57977E8D5978B3678320BD0C04CA4A6C3FCB0A15250F006B2FF
-- 115 - Report Designer completion (SQLite, services phase B2).
-- PostgreSQL counterpart: postgresql/115_report_designer.sql.
--
-- Two parts:
-- 1. The CustomReport* store itself. The designer core (ReportRepo /
--    ReportDefinition) shipped in an earlier phase but NO migration
--    ever created its tables, so the store existed only in code.
--    CREATE TABLE IF NOT EXISTS with the exact shape ReportRepo
--    reads/writes closes that gap on every database, old or new.
-- 2. The B2 completion columns: report styling + page setup on
--    CustomReportDefinitions (fonts per band, header colors, row
--    banding color, grid-lines toggle, page size, margins, company
--    address block) and TotalsFormula on CustomReportColumns — a
--    per-column expression over the column aggregates (e.g.
--    SUM(Profit) / SUM(Sales) * 100) evaluated for the totals row
--    and group subtotals at render time.
--
-- Flag columns are INTEGER (0/1): ReportRepo's shared SQL filters
-- IsDeleted = 0 on both providers.

CREATE TABLE IF NOT EXISTS CustomReportDefinitions (
    ReportDefID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
    ReportKey TEXT NOT NULL,
    ReportName TEXT NOT NULL,
    Description TEXT,
    LanguageCode TEXT NOT NULL DEFAULT 'ar',
    OwnerUserID INTEGER NOT NULL DEFAULT 0,
    Visibility TEXT NOT NULL DEFAULT 'PRIVATE',
    DataSourceKey TEXT NOT NULL DEFAULT '',
    PageOrientation TEXT NOT NULL DEFAULT 'PORTRAIT',
    ShowTotalsRow INTEGER NOT NULL DEFAULT 1,
    ShowHeaderInfo INTEGER NOT NULL DEFAULT 1,
    FreezeFirstColumn INTEGER NOT NULL DEFAULT 0,
    IsFavorite INTEGER NOT NULL DEFAULT 0,
    IsSystem INTEGER NOT NULL DEFAULT 0,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    TitleFontFamily TEXT,
    TitleFontSize REAL NOT NULL DEFAULT 20,
    TitleBold INTEGER NOT NULL DEFAULT 1,
    HeaderFontFamily TEXT,
    HeaderFontSize REAL NOT NULL DEFAULT 10.5,
    HeaderBold INTEGER NOT NULL DEFAULT 1,
    HeaderBackColor TEXT,
    HeaderTextColor TEXT,
    BodyFontFamily TEXT,
    BodyFontSize REAL NOT NULL DEFAULT 10,
    TotalsFontFamily TEXT,
    TotalsFontSize REAL NOT NULL DEFAULT 10,
    TotalsBold INTEGER NOT NULL DEFAULT 1,
    BandingColor TEXT,
    ShowGridLines INTEGER NOT NULL DEFAULT 1,
    PageSize TEXT NOT NULL DEFAULT 'A4',
    MarginMm REAL NOT NULL DEFAULT 12,
    ShowCompanyAddress INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    UpdatedBy TEXT,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt TEXT,
    DeletedAt TEXT,
    DeletedBy TEXT
);

CREATE TABLE IF NOT EXISTS CustomReportColumns (
    ReportColID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
    ReportDefID INTEGER NOT NULL,
    FieldKey TEXT NOT NULL,
    CaptionAR TEXT NOT NULL DEFAULT '',
    CaptionEN TEXT,
    DisplayOrder INTEGER NOT NULL DEFAULT 0,
    ColumnWidth NUMERIC NOT NULL DEFAULT 120,
    IsVisible INTEGER NOT NULL DEFAULT 1,
    IsLocked INTEGER NOT NULL DEFAULT 0,
    ColumnType TEXT NOT NULL DEFAULT 'DATA',
    FormulaText TEXT,
    TotalsFormula TEXT,
    DataFormat TEXT NOT NULL DEFAULT 'TEXT',
    DecimalPlaces INTEGER NOT NULL DEFAULT 2,
    AggregateFunc TEXT,
    NegativeInRed INTEGER NOT NULL DEFAULT 1,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    DeletedAt TEXT,
    DeletedBy TEXT
);

CREATE TABLE IF NOT EXISTS CustomReportGroupings (
    ReportGrpID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
    ReportDefID INTEGER NOT NULL,
    FieldKey TEXT NOT NULL,
    GroupLevel INTEGER NOT NULL DEFAULT 0,
    ShowSubTotals INTEGER NOT NULL DEFAULT 1,
    StartOnNewPage INTEGER NOT NULL DEFAULT 0,
    SortDirection TEXT NOT NULL DEFAULT 'ASC',
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    DeletedAt TEXT,
    DeletedBy TEXT
);

CREATE TABLE IF NOT EXISTS CustomReportFilters (
    ReportFilterID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
    ReportDefID INTEGER NOT NULL,
    FilterKey TEXT NOT NULL,
    FilterLabelAR TEXT NOT NULL DEFAULT '',
    Operator TEXT NOT NULL DEFAULT 'EQ',
    ValueFrom TEXT,
    ValueTo TEXT,
    IsPrompted INTEGER NOT NULL DEFAULT 0,
    IsRequired INTEGER NOT NULL DEFAULT 0,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    DeletedAt TEXT,
    DeletedBy TEXT
);

CREATE INDEX IF NOT EXISTS ix_custom_report_definitions_key
    ON CustomReportDefinitions(ReportKey, OwnerUserID, IsDeleted);
CREATE INDEX IF NOT EXISTS ix_custom_report_columns_def
    ON CustomReportColumns(ReportDefID, IsDeleted);
CREATE INDEX IF NOT EXISTS ix_custom_report_groupings_def
    ON CustomReportGroupings(ReportDefID, IsDeleted);
CREATE INDEX IF NOT EXISTS ix_custom_report_filters_def
    ON CustomReportFilters(ReportDefID, IsDeleted);

-- B2 columns for databases whose tables predate this migration
-- (created by hand or by a partial earlier state).
-- @if-column-missing CustomReportDefinitions.TitleFontFamily
ALTER TABLE CustomReportDefinitions ADD COLUMN TitleFontFamily TEXT;
-- @if-column-missing CustomReportDefinitions.TitleFontSize
ALTER TABLE CustomReportDefinitions ADD COLUMN TitleFontSize REAL NOT NULL DEFAULT 20;
-- @if-column-missing CustomReportDefinitions.TitleBold
ALTER TABLE CustomReportDefinitions ADD COLUMN TitleBold INTEGER NOT NULL DEFAULT 1;
-- @if-column-missing CustomReportDefinitions.HeaderFontFamily
ALTER TABLE CustomReportDefinitions ADD COLUMN HeaderFontFamily TEXT;
-- @if-column-missing CustomReportDefinitions.HeaderFontSize
ALTER TABLE CustomReportDefinitions ADD COLUMN HeaderFontSize REAL NOT NULL DEFAULT 10.5;
-- @if-column-missing CustomReportDefinitions.HeaderBold
ALTER TABLE CustomReportDefinitions ADD COLUMN HeaderBold INTEGER NOT NULL DEFAULT 1;
-- @if-column-missing CustomReportDefinitions.HeaderBackColor
ALTER TABLE CustomReportDefinitions ADD COLUMN HeaderBackColor TEXT;
-- @if-column-missing CustomReportDefinitions.HeaderTextColor
ALTER TABLE CustomReportDefinitions ADD COLUMN HeaderTextColor TEXT;
-- @if-column-missing CustomReportDefinitions.BodyFontFamily
ALTER TABLE CustomReportDefinitions ADD COLUMN BodyFontFamily TEXT;
-- @if-column-missing CustomReportDefinitions.BodyFontSize
ALTER TABLE CustomReportDefinitions ADD COLUMN BodyFontSize REAL NOT NULL DEFAULT 10;
-- @if-column-missing CustomReportDefinitions.TotalsFontFamily
ALTER TABLE CustomReportDefinitions ADD COLUMN TotalsFontFamily TEXT;
-- @if-column-missing CustomReportDefinitions.TotalsFontSize
ALTER TABLE CustomReportDefinitions ADD COLUMN TotalsFontSize REAL NOT NULL DEFAULT 10;
-- @if-column-missing CustomReportDefinitions.TotalsBold
ALTER TABLE CustomReportDefinitions ADD COLUMN TotalsBold INTEGER NOT NULL DEFAULT 1;
-- @if-column-missing CustomReportDefinitions.BandingColor
ALTER TABLE CustomReportDefinitions ADD COLUMN BandingColor TEXT;
-- @if-column-missing CustomReportDefinitions.ShowGridLines
ALTER TABLE CustomReportDefinitions ADD COLUMN ShowGridLines INTEGER NOT NULL DEFAULT 1;
-- @if-column-missing CustomReportDefinitions.PageSize
ALTER TABLE CustomReportDefinitions ADD COLUMN PageSize TEXT NOT NULL DEFAULT 'A4';
-- @if-column-missing CustomReportDefinitions.MarginMm
ALTER TABLE CustomReportDefinitions ADD COLUMN MarginMm REAL NOT NULL DEFAULT 12;
-- @if-column-missing CustomReportDefinitions.ShowCompanyAddress
ALTER TABLE CustomReportDefinitions ADD COLUMN ShowCompanyAddress INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing CustomReportColumns.TotalsFormula
ALTER TABLE CustomReportColumns ADD COLUMN TotalsFormula TEXT;
-- @shoutech-migration 122_recycle_bin_completion.sql BE3458B01C314142733845B34B9AF87A9EFB1803A54DE3361C49FEE343F4E257 5779FF57BEEFF9B578EC23E0C1236248CAFD9FD78DB992E7B66D3C1A9DACAB28 5779FF57BEEFF9B578EC23E0C1236248CAFD9FD78DB992E7B66D3C1A9DACAB28 5779FF57BEEFF9B578EC23E0C1236248CAFD9FD78DB992E7B66D3C1A9DACAB28
-- 122 - Recycle-bin completion (SOFT_DELETE_STUDY): bin metadata on the
-- document tables, soft-delete columns on journal_entries, and the
-- bin_posting_events exactly-once ledger. Additive only: every existing
-- consumer keys on is_deleted as before; deleted_at/deleted_by are
-- display/audit metadata for the bin, and bin_posting_events is written
-- only by DocBinSvc.

-- @if-column-missing sales_documents.deleted_at
ALTER TABLE sales_documents ADD COLUMN deleted_at TEXT;
-- @if-column-missing sales_documents.deleted_by
ALTER TABLE sales_documents ADD COLUMN deleted_by TEXT;
-- @if-column-missing purchase_documents.deleted_at
ALTER TABLE purchase_documents ADD COLUMN deleted_at TEXT;
-- @if-column-missing purchase_documents.deleted_by
ALTER TABLE purchase_documents ADD COLUMN deleted_by TEXT;
-- @if-column-missing parties.deleted_at
ALTER TABLE parties ADD COLUMN deleted_at TEXT;
-- @if-column-missing parties.deleted_by
ALTER TABLE parties ADD COLUMN deleted_by TEXT;
-- @if-column-missing units_of_measure.deleted_at
ALTER TABLE units_of_measure ADD COLUMN deleted_at TEXT;
-- @if-column-missing units_of_measure.deleted_by
ALTER TABLE units_of_measure ADD COLUMN deleted_by TEXT;
-- @if-column-missing cheques.deleted_at
ALTER TABLE cheques ADD COLUMN deleted_at TEXT;
-- @if-column-missing cheques.deleted_by
ALTER TABLE cheques ADD COLUMN deleted_by TEXT;
-- @if-column-missing pos_sessions.deleted_at
ALTER TABLE pos_sessions ADD COLUMN deleted_at TEXT;
-- @if-column-missing pos_sessions.deleted_by
ALTER TABLE pos_sessions ADD COLUMN deleted_by TEXT;

-- @if-column-missing journal_entries.is_deleted
ALTER TABLE journal_entries ADD COLUMN is_deleted INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing journal_entries.deleted_at
ALTER TABLE journal_entries ADD COLUMN deleted_at TEXT;
-- @if-column-missing journal_entries.deleted_by
ALTER TABLE journal_entries ADD COLUMN deleted_by TEXT;

-- Exactly-once ledger for bin posting compensation: a NEUTRALIZE event
-- with no later REINSTATE event means the subject is currently binned
-- with neutralized posting effects. DocBinSvc checks and writes these
-- rows inside the same transaction as the effects themselves.
CREATE TABLE IF NOT EXISTS bin_posting_events (
    event_id TEXT PRIMARY KEY,
    company_key TEXT NOT NULL,
    subject_kind TEXT NOT NULL,
    doc_table TEXT NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_key TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('NEUTRALIZE', 'REINSTATE')),
    journal_entry_id TEXT,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT
);
CREATE INDEX IF NOT EXISTS ix_bin_posting_events_subject
    ON bin_posting_events(company_key, subject_kind, doc_table, doc_key, created_at);
-- @shoutech-migration 130_maintenance_reports.sql F42D2DED05A78E479E0010598C9C4AE27D5606156DF5402C5627B3F8D2AFBC8D D960E4F287F3E1E95089163F134D5AEEF9CAE8A84687B111F1A5C574C2B5FA26 D960E4F287F3E1E95089163F134D5AEEF9CAE8A84687B111F1A5C574C2B5FA26 D960E4F287F3E1E95089163F134D5AEEF9CAE8A84687B111F1A5C574C2B5FA26
-- Immutable maintenance report history with a sequential report number.
CREATE TABLE maintenance_reports (
    report_no INTEGER PRIMARY KEY AUTOINCREMENT,
    company_code TEXT NOT NULL,
    run_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT NOT NULL,
    provider TEXT NOT NULL,
    status TEXT NOT NULL,
    check_count INTEGER NOT NULL DEFAULT 0 CHECK (check_count >= 0),
    passed_count INTEGER NOT NULL DEFAULT 0 CHECK (passed_count >= 0),
    warning_count INTEGER NOT NULL DEFAULT 0 CHECK (warning_count >= 0),
    failed_count INTEGER NOT NULL DEFAULT 0 CHECK (failed_count >= 0),
    report_json TEXT NOT NULL
);
CREATE INDEX ix_maintenance_reports_company_no
    ON maintenance_reports(company_code, report_no DESC);
