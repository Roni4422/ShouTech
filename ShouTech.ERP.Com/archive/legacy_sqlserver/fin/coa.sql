-- ═══════════════════════════════════════════════════════════════════════════
-- FILE: dba/sds/01_seed_coa.sql
-- PURPOSE: Saudi GAAP Compliant Chart of Accounts (4 Levels)
-- ═══════════════════════════════════════════════════════════════════════════

USE [$(DbFileName)];
GO

PRINT '🌱 Seeding Chart of Accounts (Saudi GAAP)...';
GO

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '1', N'الأصول', 'Assets', 'ASSET', 'DEBIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '2', N'الخصوم', 'Liabilities', 'LIABILITY', 'CREDIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '3', N'حقوق الملكية', 'Equity', 'EQUITY', 'CREDIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '4', N'الإيرادات', 'Revenue', 'REVENUE', 'CREDIT', 1, 1, 'INCOME_STATEMENT'),
('$(CompanyID)', '5', N'المصروفات', 'Expenses', 'EXPENSE', 'DEBIT', 1, 1, 'INCOME_STATEMENT');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory, IsCashAccount, IsBankAccount)
VALUES 
('$(CompanyID)', '11', '1', N'الأصول المتداولة', 'Current Assets', 'ASSET', 'DEBIT', 2, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '111', '11', N'النقدية', 'Cash & Banks', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1111', '111', N'الصندوق', 'Cash on Hand', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 1, 0),
('$(CompanyID)', '1112', '111', N'البنك الأهلي - الجاري', 'NCB Current Account', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 1),
('$(CompanyID)', '1113', '111', N'البنك الراجحي - الجاري', 'Al Rajhi Current Account', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 1),
('$(CompanyID)', '112', '11', N'الذمم المدينة', 'Accounts Receivable', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1121', '112', N'ذمم عملاء محلية', 'Local Customers AR', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1122', '112', N'ذمم عملاء تصدير', 'Export Customers AR', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '113', '11', N'مخزون البضاعة', 'Inventory', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1131', '113', N'بضاعة أول المدة', 'Opening Inventory', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1132', '113', N'المشتريات', 'Purchases', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '114', '11', N'مصروفات مدفوعة مقدماً', 'Prepaid Expenses', 'ASSET', 'DEBIT', 3, 0, 'CURRENT_ASSETS', 0, 0);

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '12', '1', N'الأصول الثابتة', 'Fixed Assets', 'ASSET', 'DEBIT', 2, 1, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '121', '12', N'الأراضي', 'Land', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '122', '12', N'المباني', 'Buildings', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '123', '12', N'الآلات والمعدات', 'Machinery & Equipment', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '124', '12', N'السيارات', 'Vehicles', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '125', '12', N'أثاث ومفروشات', 'Furniture & Fixtures', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '126', '12', N'مجمع الإهلاك', 'Accumulated Depreciation', 'ASSET', 'CREDIT', 3, 0, 'NON_CURRENT_ASSETS'); -- Contra asset

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '21', '2', N'الخصوم المتداولة', 'Current Liabilities', 'LIABILITY', 'CREDIT', 2, 1, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '211', '21', N'ذمم موردين', 'Accounts Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '212', '21', N'أوراق دفع', 'Notes Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '213', '21', N'القروض قصيرة الأجل', 'Short-term Loans', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '214', '21', N'ضريبة القيمة المضافة', 'VAT Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '215', '21', N'التأمينات الاجتماعية مستحقة', 'GOSI Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '216', '21', N'رواتب مستحقة', 'Salaries Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '31', '3', N'رأس المال', 'Capital', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '32', '3', N'الاحتياطي', 'Reserves', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '33', '3', N'أرباح مرحلة', 'Retained Earnings', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '34', '3', N'أرباح السنة الحالية', 'Current Year Profit', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '41', '4', N'إيرادات المبيعات', 'Sales Revenue', 'REVENUE', 'CREDIT', 2, 1, 'OPERATING_REVENUE'),
('$(CompanyID)', '411', '41', N'مبيعات محلية', 'Local Sales', 'REVENUE', 'CREDIT', 3, 0, 'OPERATING_REVENUE'),
('$(CompanyID)', '412', '41', N'مبيعات تصدير', 'Export Sales', 'REVENUE', 'CREDIT', 3, 0, 'OPERATING_REVENUE'),
('$(CompanyID)', '413', '41', N'مردودات مبيعات', 'Sales Returns', 'REVENUE', 'DEBIT', 3, 0, 'OPERATING_REVENUE'), -- Contra revenue
('$(CompanyID)', '42', '4', N'إيرادات أخرى', 'Other Income', 'REVENUE', 'CREDIT', 2, 0, 'OTHER_INCOME');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '51', '5', N'تكلفة البضاعة المباعة', 'Cost of Goods Sold', 'EXPENSE', 'DEBIT', 2, 1, 'COGS'),
('$(CompanyID)', '511', '51', N'بضاعة أول المدة', 'Beginning Inventory', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '512', '51', N'المشتريات', 'Purchases', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '513', '51', N'مصاريف الشحن على المشتريات', 'Freight In', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '514', '51', N'مردودات مشتريات', 'Purchase Returns', 'EXPENSE', 'CREDIT', 3, 0, 'COGS'), -- Contra expense
('$(CompanyID)', '515', '51', N'بضاعة آخر المدة', 'Ending Inventory', 'EXPENSE', 'CREDIT', 3, 0, 'COGS'), -- Contra expense

('$(CompanyID)', '61', '5', N'مصروفات التشغيل', 'Operating Expenses', 'EXPENSE', 'DEBIT', 2, 1, 'OPERATING_EXPENSES'),
('$(CompanyID)', '611', '61', N'مصروفات الرواتب', 'Salaries Expense', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '612', '61', N'التأمينات الاجتماعية (نصيب المنشأة)', 'GOSI Employer Share', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '613', '61', N'الإيجارات', 'Rent Expense', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '614', '61', N'الكهرباء والماء', 'Utilities', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '615', '61', N'الاتصالات', 'Communications', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '616', '61', N'الصيانة', 'Maintenance', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '617', '61', N'الإهلاك', 'Depreciation', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '618', '61', N'مصروفات تسويقية', 'Marketing', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '619', '61', N'مصروفات إدارية', 'Administrative', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES');

PRINT '✅ Chart of Accounts Seeded (55 Accounts)';
GO