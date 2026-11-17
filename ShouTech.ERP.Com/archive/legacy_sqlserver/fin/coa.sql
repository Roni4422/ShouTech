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
('$(CompanyID)', '1', N'Assets', 'Assets', 'ASSET', 'DEBIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '2', N'Liabilities', 'Liabilities', 'LIABILITY', 'CREDIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '3', N'Equity', 'Equity', 'EQUITY', 'CREDIT', 1, 1, 'BALANCE_SHEET'),
('$(CompanyID)', '4', N'Revenue', 'Revenue', 'REVENUE', 'CREDIT', 1, 1, 'INCOME_STATEMENT'),
('$(CompanyID)', '5', N'Expenses', 'Expenses', 'EXPENSE', 'DEBIT', 1, 1, 'INCOME_STATEMENT');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory, IsCashAccount, IsBankAccount)
VALUES 
('$(CompanyID)', '11', '1', N'Current Assets', 'Current Assets', 'ASSET', 'DEBIT', 2, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '111', '11', N'Cash', 'Cash & Banks', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1111', '111', N'Cash', 'Cash on Hand', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 1, 0),
('$(CompanyID)', '1112', '111', N'AlAhli Bank - Current', 'NCB Current Account', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 1),
('$(CompanyID)', '1113', '111', N'AlRajhi Bank - Current', 'Al Rajhi Current Account', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 1),
('$(CompanyID)', '112', '11', N'Accounts Receivable', 'Accounts Receivable', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1121', '112', N'Local Customer Receivables', 'Local Customers AR', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1122', '112', N'Export Customer Receivables', 'Export Customers AR', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '113', '11', N'Inventory', 'Inventory', 'ASSET', 'DEBIT', 3, 1, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1131', '113', N'Opening Stock', 'Opening Inventory', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '1132', '113', N'Purchases', 'Purchases', 'ASSET', 'DEBIT', 4, 0, 'CURRENT_ASSETS', 0, 0),
('$(CompanyID)', '114', '11', N'Prepaid Expenses', 'Prepaid Expenses', 'ASSET', 'DEBIT', 3, 0, 'CURRENT_ASSETS', 0, 0);

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '12', '1', N'Fixed Assets', 'Fixed Assets', 'ASSET', 'DEBIT', 2, 1, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '121', '12', N'Land', 'Land', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '122', '12', N'Buildings', 'Buildings', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '123', '12', N'Machinery and Equipment', 'Machinery & Equipment', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '124', '12', N'Vehicles', 'Vehicles', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '125', '12', N'Furniture and Fixtures', 'Furniture & Fixtures', 'ASSET', 'DEBIT', 3, 0, 'NON_CURRENT_ASSETS'),
('$(CompanyID)', '126', '12', N'Accumulated Depreciation', 'Accumulated Depreciation', 'ASSET', 'CREDIT', 3, 0, 'NON_CURRENT_ASSETS'); -- Contra asset

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '21', '2', N'Current Liabilities', 'Current Liabilities', 'LIABILITY', 'CREDIT', 2, 1, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '211', '21', N'Supplier Payables', 'Accounts Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '212', '21', N'Notes Payable', 'Notes Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '213', '21', N'Short-Term Loans', 'Short-term Loans', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '214', '21', N'Value Added Tax', 'VAT Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '215', '21', N'Social Insurance Payable', 'GOSI Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES'),
('$(CompanyID)', '216', '21', N'Accrued Salaries', 'Salaries Payable', 'LIABILITY', 'CREDIT', 3, 0, 'CURRENT_LIABILITIES');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '31', '3', N'Capital', 'Capital', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '32', '3', N'Reserves', 'Reserves', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '33', '3', N'Retained Earnings', 'Retained Earnings', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY'),
('$(CompanyID)', '34', '3', N'Current Year Profit', 'Current Year Profit', 'EQUITY', 'CREDIT', 2, 0, 'EQUITY');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '41', '4', N'Sales Revenue', 'Sales Revenue', 'REVENUE', 'CREDIT', 2, 1, 'OPERATING_REVENUE'),
('$(CompanyID)', '411', '41', N'Local Sales', 'Local Sales', 'REVENUE', 'CREDIT', 3, 0, 'OPERATING_REVENUE'),
('$(CompanyID)', '412', '41', N'Export Sales', 'Export Sales', 'REVENUE', 'CREDIT', 3, 0, 'OPERATING_REVENUE'),
('$(CompanyID)', '413', '41', N'Sales Returns', 'Sales Returns', 'REVENUE', 'DEBIT', 3, 0, 'OPERATING_REVENUE'), -- Contra revenue
('$(CompanyID)', '42', '4', N'Other Revenue', 'Other Income', 'REVENUE', 'CREDIT', 2, 0, 'OTHER_INCOME');

INSERT INTO dbo.ChartOfAccounts (CompanyID, AccountCode, ParentAccountCode, AccountNameAR, AccountNameEN, AccountType, AccountNature, AccountLevel, IsHeader, FinancialStatementCategory)
VALUES 
('$(CompanyID)', '51', '5', N'Cost of Goods Sold', 'Cost of Goods Sold', 'EXPENSE', 'DEBIT', 2, 1, 'COGS'),
('$(CompanyID)', '511', '51', N'Opening Stock', 'Beginning Inventory', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '512', '51', N'Purchases', 'Purchases', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '513', '51', N'Freight on Purchases', 'Freight In', 'EXPENSE', 'DEBIT', 3, 0, 'COGS'),
('$(CompanyID)', '514', '51', N'Purchase Returns', 'Purchase Returns', 'EXPENSE', 'CREDIT', 3, 0, 'COGS'), -- Contra expense
('$(CompanyID)', '515', '51', N'Closing Stock', 'Ending Inventory', 'EXPENSE', 'CREDIT', 3, 0, 'COGS'), -- Contra expense

('$(CompanyID)', '61', '5', N'Operating Expenses', 'Operating Expenses', 'EXPENSE', 'DEBIT', 2, 1, 'OPERATING_EXPENSES'),
('$(CompanyID)', '611', '61', N'Payroll Expenses', 'Salaries Expense', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '612', '61', N'Social Insurance (Employer Share)', 'GOSI Employer Share', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '613', '61', N'Rent', 'Rent Expense', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '614', '61', N'Electricity and Water', 'Utilities', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '615', '61', N'Telecommunications', 'Communications', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '616', '61', N'Maintenance', 'Maintenance', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '617', '61', N'Depreciation', 'Depreciation', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '618', '61', N'Marketing Expenses', 'Marketing', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES'),
('$(CompanyID)', '619', '61', N'Administrative Expenses', 'Administrative', 'EXPENSE', 'DEBIT', 3, 0, 'OPERATING_EXPENSES');

PRINT '✅ Chart of Accounts Seeded (55 Accounts)';
GO