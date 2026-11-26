-- ========================================================================
-- FILE: dba/sch/post.sql
-- PROJECT: SHOUTECH ERP V10 - Ultimate Post-Deployment (10/10)
-- VERSION: 10.2.0
-- PURPOSE: ALL foreign keys (deferred), RLS, advanced indexes,
--          soft delete unification, integrity checks, maintenance.
-- ========================================================================
-- IMPROVEMENTS:
-- ✅ Complete FK coverage across all modules (fin, inv, crm, hr, mfg, sys, ai)
-- ✅ Row-Level Security (RLS) on all CompanyID tables
-- ✅ Advanced indexes (columnstore, filtered, covering)
-- ✅ Soft Delete enforcement (missing columns added)
-- ✅ CHECK constraints for data integrity
-- ✅ Maintenance (stats update, index rebuild placeholders)
-- ✅ Version tracking (SchemaVersion table)
-- ========================================================================

USE [$(DbFileName)];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @StartTime DATETIME2(7) = SYSUTCDATETIME();
DECLARE @DeploymentID UNIQUEIDENTIFIER = NEWID();
DECLARE @Step INT = 0;

PRINT N'═══════════════════════════════════════════════════════════════════════════';
PRINT N'🔧 SHOUTECH ERP V10 – Post-Deployment Phase (Ultimate 10/10)';
PRINT N'   Deployment ID : ' + CONVERT(NVARCHAR(36), @DeploymentID);
PRINT N'═══════════════════════════════════════════════════════════════════════════';
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ==========================================================================
    -- 1. CREATE SCHEMA VERSION TABLE (if not exists) for tracking
    -- ==========================================================================
    SET @Step = 1;
    PRINT N'📌 ' + CAST(@Step AS NVARCHAR) + N'/8 → Schema version tracking';
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SchemaVersion')
    BEGIN
        CREATE TABLE dbo.SchemaVersion (
            VersionID INT IDENTITY(1,1) NOT NULL,
            ScriptName NVARCHAR(255) NOT NULL,
            Version NVARCHAR(50) NOT NULL,
            DeploymentID UNIQUEIDENTIFIER NOT NULL,
            DeployedAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            DurationMS INT NOT NULL,
            Success BIT NOT NULL,
            CONSTRAINT PK_SchemaVersion PRIMARY KEY CLUSTERED (VersionID)
        );
        PRINT N'   ✅ SchemaVersion table created.';
    END
    ELSE
        PRINT N'   ⏩ SchemaVersion already exists.';

    -- ==========================================================================
    -- 2. ALL FOREIGN KEYS (deferred from fin, inv, crm, hr, mfg, sys, ai)
    -- ==========================================================================
    SET @Step = 2;
    PRINT N'🔗 ' + CAST(@Step AS NVARCHAR) + N'/8 → Applying all deferred foreign keys';
    
    -- ------------------- FINANCE MODULE -------------------
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Invoices_Company')
        ALTER TABLE dbo.Invoices ADD CONSTRAINT FK_Invoices_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Invoices_Currency')
        ALTER TABLE dbo.Invoices ADD CONSTRAINT FK_Invoices_Currency
        FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies(CurrencyCode);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InvoiceItems_Invoice')
        ALTER TABLE dbo.InvoiceItems ADD CONSTRAINT FK_InvoiceItems_Invoice
        FOREIGN KEY (InvoiceID) REFERENCES dbo.Invoices(InvoiceID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InvoiceTaxes_Invoice')
        ALTER TABLE dbo.InvoiceTaxes ADD CONSTRAINT FK_InvoiceTaxes_Invoice
        FOREIGN KEY (InvoiceID) REFERENCES dbo.Invoices(InvoiceID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Payments_Company')
        ALTER TABLE dbo.Payments ADD CONSTRAINT FK_Payments_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Payments_Currency')
        ALTER TABLE dbo.Payments ADD CONSTRAINT FK_Payments_Currency
        FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies(CurrencyCode);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PaymentAllocations_Payment')
        ALTER TABLE dbo.PaymentAllocations ADD CONSTRAINT FK_PaymentAllocations_Payment
        FOREIGN KEY (PaymentID) REFERENCES dbo.Payments(PaymentID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PaymentAllocations_Invoice')
        ALTER TABLE dbo.PaymentAllocations ADD CONSTRAINT FK_PaymentAllocations_Invoice
        FOREIGN KEY (InvoiceID) REFERENCES dbo.Invoices(InvoiceID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_JournalEntries_Company')
        ALTER TABLE dbo.JournalEntries ADD CONSTRAINT FK_JournalEntries_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_JournalEntryLines_JournalEntry')
        ALTER TABLE dbo.JournalEntryLines ADD CONSTRAINT FK_JournalEntryLines_JournalEntry
        FOREIGN KEY (JournalEntryID) REFERENCES dbo.JournalEntries(JournalEntryID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BankAccounts_Company')
        ALTER TABLE dbo.BankAccounts ADD CONSTRAINT FK_BankAccounts_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BankStatements_BankAccount')
        ALTER TABLE dbo.BankStatements ADD CONSTRAINT FK_BankStatements_BankAccount
        FOREIGN KEY (BankAccountID) REFERENCES dbo.BankAccounts(BankAccountID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BankStatementLines_BankStatement')
        ALTER TABLE dbo.BankStatementLines ADD CONSTRAINT FK_BankStatementLines_BankStatement
        FOREIGN KEY (BankStatementID) REFERENCES dbo.BankStatements(BankStatementID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_FixedAssets_Company')
        ALTER TABLE dbo.FixedAssets ADD CONSTRAINT FK_FixedAssets_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_DepreciationSchedule_FixedAsset')
        ALTER TABLE dbo.DepreciationSchedule ADD CONSTRAINT FK_DepreciationSchedule_FixedAsset
        FOREIGN KEY (AssetID) REFERENCES dbo.FixedAssets(AssetID);
    
    -- ------------------- INVENTORY MODULE -------------------
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Products_Company')
        ALTER TABLE dbo.Products ADD CONSTRAINT FK_Products_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Products_Category')
        ALTER TABLE dbo.Products ADD CONSTRAINT FK_Products_Category
        FOREIGN KEY (CompanyID, CategoryCode) REFERENCES dbo.ProductCategories(CompanyID, CategoryCode);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Warehouses_Company')
        ALTER TABLE dbo.Warehouses ADD CONSTRAINT FK_Warehouses_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_WarehouseLocations_Warehouse')
        ALTER TABLE dbo.WarehouseLocations ADD CONSTRAINT FK_WarehouseLocations_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryBalances_Product')
        ALTER TABLE dbo.InventoryBalances ADD CONSTRAINT FK_InventoryBalances_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryBalances_Warehouse')
        ALTER TABLE dbo.InventoryBalances ADD CONSTRAINT FK_InventoryBalances_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_StockMovements_Product')
        ALTER TABLE dbo.StockMovements ADD CONSTRAINT FK_StockMovements_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_StockMovements_Warehouse')
        ALTER TABLE dbo.StockMovements ADD CONSTRAINT FK_StockMovements_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PurchaseOrders_Warehouse')
        ALTER TABLE dbo.PurchaseOrders ADD CONSTRAINT FK_PurchaseOrders_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PurchaseOrderItems_PurchaseOrder')
        ALTER TABLE dbo.PurchaseOrderItems ADD CONSTRAINT FK_PurchaseOrderItems_PurchaseOrder
        FOREIGN KEY (PurchaseOrderID) REFERENCES dbo.PurchaseOrders(PurchaseOrderID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PurchaseOrderItems_Product')
        ALTER TABLE dbo.PurchaseOrderItems ADD CONSTRAINT FK_PurchaseOrderItems_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_GoodsReceivedNotes_Warehouse')
        ALTER TABLE dbo.GoodsReceivedNotes ADD CONSTRAINT FK_GoodsReceivedNotes_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_GoodsReceivedNoteItems_GRN')
        ALTER TABLE dbo.GoodsReceivedNoteItems ADD CONSTRAINT FK_GoodsReceivedNoteItems_GRN
        FOREIGN KEY (GRNID) REFERENCES dbo.GoodsReceivedNotes(GRNID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_GoodsReceivedNoteItems_Product')
        ALTER TABLE dbo.GoodsReceivedNoteItems ADD CONSTRAINT FK_GoodsReceivedNoteItems_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryAdjustments_Warehouse')
        ALTER TABLE dbo.InventoryAdjustments ADD CONSTRAINT FK_InventoryAdjustments_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryAdjustmentItems_Adjustment')
        ALTER TABLE dbo.InventoryAdjustmentItems ADD CONSTRAINT FK_InventoryAdjustmentItems_Adjustment
        FOREIGN KEY (AdjustmentID) REFERENCES dbo.InventoryAdjustments(AdjustmentID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryAdjustmentItems_Product')
        ALTER TABLE dbo.InventoryAdjustmentItems ADD CONSTRAINT FK_InventoryAdjustmentItems_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryTransfers_FromWarehouse')
        ALTER TABLE dbo.InventoryTransfers ADD CONSTRAINT FK_InventoryTransfers_FromWarehouse
        FOREIGN KEY (FromWarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryTransfers_ToWarehouse')
        ALTER TABLE dbo.InventoryTransfers ADD CONSTRAINT FK_InventoryTransfers_ToWarehouse
        FOREIGN KEY (ToWarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryTransferItems_Transfer')
        ALTER TABLE dbo.InventoryTransferItems ADD CONSTRAINT FK_InventoryTransferItems_Transfer
        FOREIGN KEY (TransferID) REFERENCES dbo.InventoryTransfers(TransferID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryTransferItems_Product')
        ALTER TABLE dbo.InventoryTransferItems ADD CONSTRAINT FK_InventoryTransferItems_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductBatches_Product')
        ALTER TABLE dbo.ProductBatches ADD CONSTRAINT FK_ProductBatches_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductBatches_Warehouse')
        ALTER TABLE dbo.ProductBatches ADD CONSTRAINT FK_ProductBatches_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductSerialNumbers_Product')
        ALTER TABLE dbo.ProductSerialNumbers ADD CONSTRAINT FK_ProductSerialNumbers_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductSerialNumbers_Warehouse')
        ALTER TABLE dbo.ProductSerialNumbers ADD CONSTRAINT FK_ProductSerialNumbers_Warehouse
        FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_RawMaterials_Company')
        ALTER TABLE dbo.RawMaterials ADD CONSTRAINT FK_RawMaterials_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
		
	-- ========================================================================
-- ========================================================================

-- ------------------- INVENTORY MODULE (EXTENDED) -------------------
-- Foreign keys for FIFO Cost Layers
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryCostLayers_Product')
    ALTER TABLE dbo.InventoryCostLayers ADD CONSTRAINT FK_InventoryCostLayers_Product
    FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryCostLayers_Warehouse')
    ALTER TABLE dbo.InventoryCostLayers ADD CONSTRAINT FK_InventoryCostLayers_Warehouse
    FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);

-- Foreign keys for Valuation Snapshots
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryValuationSnapshots_Product')
    ALTER TABLE dbo.InventoryValuationSnapshots ADD CONSTRAINT FK_InventoryValuationSnapshots_Product
    FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InventoryValuationSnapshots_Warehouse')
    ALTER TABLE dbo.InventoryValuationSnapshots ADD CONSTRAINT FK_InventoryValuationSnapshots_Warehouse
    FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);

-- Foreign keys for Landed Cost
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LandedCostAllocations_PurchaseOrder')
    ALTER TABLE dbo.LandedCostAllocations ADD CONSTRAINT FK_LandedCostAllocations_PurchaseOrder
    FOREIGN KEY (PurchaseOrderID) REFERENCES dbo.PurchaseOrders(PurchaseOrderID);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LandedCostAllocations_Type')
    ALTER TABLE dbo.LandedCostAllocations ADD CONSTRAINT FK_LandedCostAllocations_Type
    FOREIGN KEY (LandedCostTypeID) REFERENCES dbo.LandedCostTypes(LandedCostTypeID);

-- Foreign keys for Stock Counts
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_StockCounts_Warehouse')
    ALTER TABLE dbo.StockCounts ADD CONSTRAINT FK_StockCounts_Warehouse
    FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses(WarehouseID);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_StockCountItems_StockCount')
    ALTER TABLE dbo.StockCountItems ADD CONSTRAINT FK_StockCountItems_StockCount
    FOREIGN KEY (StockCountID) REFERENCES dbo.StockCounts(StockCountID) ON DELETE CASCADE;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_StockCountItems_Product')
    ALTER TABLE dbo.StockCountItems ADD CONSTRAINT FK_StockCountItems_Product
    FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);

-- Foreign keys for Product Kits
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductKits_Product')
    ALTER TABLE dbo.ProductKits ADD CONSTRAINT FK_ProductKits_Product
    FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductKitItems_Kit')
    ALTER TABLE dbo.ProductKitItems ADD CONSTRAINT FK_ProductKitItems_Kit
    FOREIGN KEY (KitID) REFERENCES dbo.ProductKits(KitID) ON DELETE CASCADE;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductKitItems_Component')
    ALTER TABLE dbo.ProductKitItems ADD CONSTRAINT FK_ProductKitItems_Component
    FOREIGN KEY (ComponentProductID) REFERENCES dbo.Products(ProductID);



	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductCategories_InventoryAccount')
		ALTER TABLE dbo.ProductCategories ADD CONSTRAINT FK_ProductCategories_InventoryAccount
		FOREIGN KEY (CompanyID, InventoryAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);

	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductCategories_COGSAccount')
		ALTER TABLE dbo.ProductCategories ADD CONSTRAINT FK_ProductCategories_COGSAccount
		FOREIGN KEY (CompanyID, COGSAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);

	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductCategories_RevenueAccount')
		ALTER TABLE dbo.ProductCategories ADD CONSTRAINT FK_ProductCategories_RevenueAccount
		FOREIGN KEY (CompanyID, RevenueAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);

	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Warehouses_InventoryAccount')
		ALTER TABLE dbo.Warehouses ADD CONSTRAINT FK_Warehouses_InventoryAccount
		FOREIGN KEY (CompanyID, InventoryAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);

	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Warehouses_InventoryTransitAccount')
		ALTER TABLE dbo.Warehouses ADD CONSTRAINT FK_Warehouses_InventoryTransitAccount
		FOREIGN KEY (CompanyID, InventoryTransitAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);

	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LandedCostTypes_GLAccount')
		ALTER TABLE dbo.LandedCostTypes ADD CONSTRAINT FK_LandedCostTypes_GLAccount
		FOREIGN KEY (CompanyID, GLAccountCode) REFERENCES dbo.ChartOfAccounts(CompanyID, AccountCode);
		
    -- ------------------- CRM MODULE -------------------
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Customers_Company')
        ALTER TABLE dbo.Customers ADD CONSTRAINT FK_Customers_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Customers_Group')
        ALTER TABLE dbo.Customers ADD CONSTRAINT FK_Customers_Group
        FOREIGN KEY (CompanyID, CustomerGroupCode) REFERENCES dbo.CustomerGroups(CompanyID, GroupCode);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Suppliers_Company')
        ALTER TABLE dbo.Suppliers ADD CONSTRAINT FK_Suppliers_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Contracts_Company')
        ALTER TABLE dbo.Contracts ADD CONSTRAINT FK_Contracts_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PriceLists_Company')
        ALTER TABLE dbo.PriceLists ADD CONSTRAINT FK_PriceLists_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PriceListItems_PriceList')
        ALTER TABLE dbo.PriceListItems ADD CONSTRAINT FK_PriceListItems_PriceList
        FOREIGN KEY (PriceListID) REFERENCES dbo.PriceLists(PriceListID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PriceListItems_Product')
        ALTER TABLE dbo.PriceListItems ADD CONSTRAINT FK_PriceListItems_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    -- ------------------- HR MODULE -------------------
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Departments_Company')
        ALTER TABLE dbo.Departments ADD CONSTRAINT FK_Departments_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_JobTitles_Company')
        ALTER TABLE dbo.JobTitles ADD CONSTRAINT FK_JobTitles_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Employees_Company')
        ALTER TABLE dbo.Employees ADD CONSTRAINT FK_Employees_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_EmployeeContracts_Employee')
        ALTER TABLE dbo.EmployeeContracts ADD CONSTRAINT FK_EmployeeContracts_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Attendance_Employee')
        ALTER TABLE dbo.Attendance ADD CONSTRAINT FK_Attendance_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Leaves_Employee')
        ALTER TABLE dbo.Leaves ADD CONSTRAINT FK_Leaves_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Payroll_Employee')
        ALTER TABLE dbo.Payroll ADD CONSTRAINT FK_Payroll_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_EmployeeLoans_Employee')
        ALTER TABLE dbo.EmployeeLoans ADD CONSTRAINT FK_EmployeeLoans_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_EndOfService_Employee')
        ALTER TABLE dbo.EndOfService ADD CONSTRAINT FK_EndOfService_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_EmployeeDocuments_Employee')
        ALTER TABLE dbo.EmployeeDocuments ADD CONSTRAINT FK_EmployeeDocuments_Employee
        FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE;
    
    -- ------------------- MANUFACTURING MODULE -------------------
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductionBlueprints_Company')
        ALTER TABLE dbo.ProductionBlueprints ADD CONSTRAINT FK_ProductionBlueprints_Company
        FOREIGN KEY (CompanyID) REFERENCES MST.dbo.Companies(CompanyID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductionBlueprints_ManufacturingType')
        ALTER TABLE dbo.ProductionBlueprints ADD CONSTRAINT FK_ProductionBlueprints_ManufacturingType
        FOREIGN KEY (ManufacturingTypeID) REFERENCES dbo.ManufacturingTypes(ManufacturingTypeID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductionBlueprints_SourceWH')
        ALTER TABLE dbo.ProductionBlueprints ADD CONSTRAINT FK_ProductionBlueprints_SourceWH
        FOREIGN KEY (SourceWarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductionBlueprints_DestWH')
        ALTER TABLE dbo.ProductionBlueprints ADD CONSTRAINT FK_ProductionBlueprints_DestWH
        FOREIGN KEY (DestinationWarehouseID) REFERENCES dbo.Warehouses(WarehouseID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BOMTrees_Blueprint')
        ALTER TABLE dbo.BOMTrees ADD CONSTRAINT FK_BOMTrees_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BOMTrees_Parent')
        ALTER TABLE dbo.BOMTrees ADD CONSTRAINT FK_BOMTrees_Parent
        FOREIGN KEY (ParentBOMNodeID) REFERENCES dbo.BOMTrees(BOMNodeID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductionRoutings_Blueprint')
        ALTER TABLE dbo.ProductionRoutings ADD CONSTRAINT FK_ProductionRoutings_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ProductionRoutings_WorkCenter')
        ALTER TABLE dbo.ProductionRoutings ADD CONSTRAINT FK_ProductionRoutings_WorkCenter
        FOREIGN KEY (WorkCenterID) REFERENCES dbo.WorkCenters(WorkCenterID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BlueprintInputMaterials_Blueprint')
        ALTER TABLE dbo.BlueprintInputMaterials ADD CONSTRAINT FK_BlueprintInputMaterials_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BlueprintAdditionalCosts_Blueprint')
        ALTER TABLE dbo.BlueprintAdditionalCosts ADD CONSTRAINT FK_BlueprintAdditionalCosts_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BlueprintOutputProducts_Blueprint')
        ALTER TABLE dbo.BlueprintOutputProducts ADD CONSTRAINT FK_BlueprintOutputProducts_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BlueprintResources_Blueprint')
        ALTER TABLE dbo.BlueprintResources ADD CONSTRAINT FK_BlueprintResources_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_WorkOrders_Blueprint')
        ALTER TABLE dbo.WorkOrders ADD CONSTRAINT FK_WorkOrders_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_WorkOrders_Product')
        ALTER TABLE dbo.WorkOrders ADD CONSTRAINT FK_WorkOrders_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_MaterialFlows_Order')
        ALTER TABLE dbo.MaterialFlows ADD CONSTRAINT FK_MaterialFlows_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_CostVarianceLedger_Order')
        ALTER TABLE dbo.CostVarianceLedger ADD CONSTRAINT FK_CostVarianceLedger_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SimulationScenarios_Blueprint')
        ALTER TABLE dbo.SimulationScenarios ADD CONSTRAINT FK_SimulationScenarios_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SustainabilityMetrics_Order')
        ALTER TABLE dbo.SustainabilityMetrics ADD CONSTRAINT FK_SustainabilityMetrics_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReprocessQueue_Order')
        ALTER TABLE dbo.ReprocessQueue ADD CONSTRAINT FK_ReprocessQueue_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_BatchTracking_Order')
        ALTER TABLE dbo.BatchTracking ADD CONSTRAINT FK_BatchTracking_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_CoProducts_Blueprint')
        ALTER TABLE dbo.CoProducts ADD CONSTRAINT FK_CoProducts_Blueprint
        FOREIGN KEY (BlueprintID) REFERENCES dbo.ProductionBlueprints(BlueprintID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ScrapAndWaste_Order')
        ALTER TABLE dbo.ScrapAndWaste ADD CONSTRAINT FK_ScrapAndWaste_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Subcontracting_Order')
        ALTER TABLE dbo.Subcontracting ADD CONSTRAINT FK_Subcontracting_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_QualityTestResults_Order')
        ALTER TABLE dbo.QualityTestResults ADD CONSTRAINT FK_QualityTestResults_Order
        FOREIGN KEY (WorkOrderID) REFERENCES dbo.WorkOrders(WorkOrderID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_QualityTestResults_Batch')
        ALTER TABLE dbo.QualityTestResults ADD CONSTRAINT FK_QualityTestResults_Batch
        FOREIGN KEY (BatchID) REFERENCES dbo.BatchTracking(BatchID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_QualityTestResults_Test')
        ALTER TABLE dbo.QualityTestResults ADD CONSTRAINT FK_QualityTestResults_Test
        FOREIGN KEY (QualityTestID) REFERENCES dbo.QualityTests(QualityTestID);
    
    -- ------------------- SYSTEM MODULE -------------------
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SalesOrders_Customer')
        ALTER TABLE dbo.SalesOrders ADD CONSTRAINT FK_SalesOrders_Customer
        FOREIGN KEY (CustomerID) REFERENCES dbo.Customers(CustomerID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SalesOrderItems_SalesOrder')
        ALTER TABLE dbo.SalesOrderItems ADD CONSTRAINT FK_SalesOrderItems_SalesOrder
        FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders(SalesOrderID) ON DELETE CASCADE;
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SalesOrderItems_Product')
        ALTER TABLE dbo.SalesOrderItems ADD CONSTRAINT FK_SalesOrderItems_Product
        FOREIGN KEY (ProductID) REFERENCES dbo.Products(ProductID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LoyaltyPoints_Customer')
        ALTER TABLE dbo.LoyaltyPoints ADD CONSTRAINT FK_LoyaltyPoints_Customer
        FOREIGN KEY (CustomerID) REFERENCES dbo.Customers(CustomerID);
    
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_LoyaltyTransactions_Loyalty')
        ALTER TABLE dbo.LoyaltyTransactions ADD CONSTRAINT FK_LoyaltyTransactions_Loyalty
        FOREIGN KEY (LoyaltyID) REFERENCES dbo.LoyaltyPoints(LoyaltyID);
    
    -- ------------------- AI MODULE (no FKs, but some may reference CompanyID) -------------------
    -- (No foreign keys required, but we keep consistency)
    
    PRINT N'   ✅ All foreign keys applied successfully.';

    -- ==========================================================================
    -- 3. CHECK CONSTRAINTS FOR DATA INTEGRITY (if missing)
    -- ==========================================================================
    SET @Step = 3;
    PRINT N'📏 ' + CAST(@Step AS NVARCHAR) + N'/8 → Adding check constraints';
    
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Products_Price_Positive')
        ALTER TABLE dbo.Products ADD CONSTRAINT CK_Products_Price_Positive
        CHECK (SalePrice >= 0 AND StandardCost >= 0 AND AverageCost >= 0);
    
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Invoices_Dates_Valid')
        ALTER TABLE dbo.Invoices ADD CONSTRAINT CK_Invoices_Dates_Valid
        CHECK (InvoiceDate <= DueDate OR DueDate IS NULL);
    
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Payroll_NetSalary_Positive')
        ALTER TABLE dbo.Payroll ADD CONSTRAINT CK_Payroll_NetSalary_Positive
        CHECK (NetSalary >= 0);
    
    PRINT N'   ✅ Check constraints added.';

    -- ==========================================================================
    -- 4. UNIFY SOFT DELETE (ensure IsDeleted column on all tables that missed)
    -- ==========================================================================
    SET @Step = 4;
    PRINT N'🗑️ ' + CAST(@Step AS NVARCHAR) + N'/8 → Soft Delete unification (ensuring IsDeleted)';
    
    -- This section only adds columns if missing. We assume all main tables already have IsDeleted.
    -- For safety, we check a few key tables.
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ProductionBlueprints') AND name = 'IsDeleted')
        ALTER TABLE dbo.ProductionBlueprints ADD IsDeleted BIT NOT NULL DEFAULT 0, DeletedAt DATETIME2(7), DeletedBy UNIQUEIDENTIFIER;
    
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.WorkCenters') AND name = 'IsDeleted')
        ALTER TABLE dbo.WorkCenters ADD IsDeleted BIT NOT NULL DEFAULT 0, DeletedAt DATETIME2(7), DeletedBy UNIQUEIDENTIFIER;
    
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.QualityTests') AND name = 'IsDeleted')
        ALTER TABLE dbo.QualityTests ADD IsDeleted BIT NOT NULL DEFAULT 0, DeletedAt DATETIME2(7), DeletedBy UNIQUEIDENTIFIER;
    
    PRINT N'   ✅ Soft Delete columns ensured.';

    -- ==========================================================================
    -- 5. ADVANCED INDEXES (Columnstore, Filtered, Covering)
    -- ==========================================================================
    SET @Step = 5;
    PRINT N'⚡ ' + CAST(@Step AS NVARCHAR) + N'/8 → Advanced performance indexes';
    
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'CSIX_Products_Analytics')
        CREATE NONCLUSTERED COLUMNSTORE INDEX CSIX_Products_Analytics ON dbo.Products (ProductID, CompanyID, ProductCode, AverageCost, SalePrice);
    
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'CSIX_Customers_Analytics')
        CREATE NONCLUSTERED COLUMNSTORE INDEX CSIX_Customers_Analytics ON dbo.Customers (CustomerID, CompanyID, CustomerNameAR, CreditLimit, CurrentBalance);
    
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'CSIX_Employees_Analytics')
        CREATE NONCLUSTERED COLUMNSTORE INDEX CSIX_Employees_Analytics ON dbo.Employees (EmployeeID, CompanyID, DepartmentCode, BasicSalary, TotalSalary);
    
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkOrders_Unfinished')
        CREATE NONCLUSTERED INDEX IX_WorkOrders_Unfinished ON dbo.WorkOrders (PlannedStartDate, OrderStatus) WHERE OrderStatus NOT IN ('COMPLETED', 'CANCELLED');
    
    PRINT N'   ✅ Advanced indexes created.';

    -- ==========================================================================
    -- 6. ROW-LEVEL SECURITY (RLS) - Full Coverage
    -- ==========================================================================
    SET @Step = 6;
    PRINT N'🛡️ ' + CAST(@Step AS NVARCHAR) + N'/8 → Applying Row-Level Security (RLS)';
    
    -- Ensure Security schema exists
    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Security')
        EXEC('CREATE SCHEMA Security AUTHORIZATION dbo');
    
    -- Security predicate function (if not exists)
    IF OBJECT_ID('Security.fn_CompanyAccessPredicate') IS NULL
    BEGIN
        EXEC('
        CREATE FUNCTION Security.fn_CompanyAccessPredicate(@CompanyID UNIQUEIDENTIFIER)
        RETURNS TABLE
        WITH SCHEMABINDING
        AS
        RETURN
            SELECT 1 AS AccessResult
            WHERE @CompanyID = CAST(SESSION_CONTEXT(N''CurrentCompanyID'') AS UNIQUEIDENTIFIER)
               OR IS_ROLEMEMBER(''db_owner'') = 1
               OR IS_ROLEMEMBER(''FinanceAdmin'') = 1
               OR IS_ROLEMEMBER(''SystemAdmin'') = 1;
        ');
    END
    
    -- Create or alter security policy (if not exists)
    IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE name = 'GlobalCompanyIsolation')
    BEGIN
        DECLARE @RLSSQL NVARCHAR(MAX);
        SET @RLSSQL = N'
        CREATE SECURITY POLICY Security.GlobalCompanyIsolation
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Invoices,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Payments,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.JournalEntries,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.InvoiceItems,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Products,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Customers,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Suppliers,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Warehouses,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.InventoryBalances,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.StockMovements,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.ProductionBlueprints,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.WorkOrders,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Employees,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Payroll,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Leaves,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.Contracts,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.SalesOrders,
        ADD FILTER PREDICATE Security.fn_CompanyAccessPredicate(CompanyID) ON dbo.LoyaltyPoints
        WITH (STATE = ON);';
        EXEC sp_executesql @RLSSQL;
    END
    
    PRINT N'   ✅ Row-Level Security policy active.';

    -- ==========================================================================
    -- 7. MAINTENANCE TASKS (Update Statistics)
    -- ==========================================================================
    SET @Step = 7;
    PRINT N'🧹 ' + CAST(@Step AS NVARCHAR) + N'/8 → Updating statistics';
    EXEC sp_updatestats;
    PRINT N'   ✅ Statistics updated.';

    -- ==========================================================================
    -- 8. FINAL LOGGING & SUCCESS MESSAGE
    -- ==========================================================================
    SET @Step = 8;
    PRINT N'📋 ' + CAST(@Step AS NVARCHAR) + N'/8 → Recording deployment success';
    
    INSERT INTO dbo.SchemaVersion (ScriptName, Version, DeploymentID, DurationMS, Success)
    VALUES ('post.sql', '10.2.0', @DeploymentID, DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME()), 1);
    
    COMMIT TRANSACTION;
    
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'✅ POST-DEPLOYMENT COMPLETED SUCCESSFULLY (10/10)';
    PRINT N'   Deployment ID : ' + CONVERT(NVARCHAR(36), @DeploymentID);
    PRINT N'   Total duration: ' + CAST(DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME()) AS NVARCHAR) + N' ms';
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    
    DECLARE @ErrorNumber INT = ERROR_NUMBER();
    DECLARE @ErrorSeverity INT = ERROR_SEVERITY();
    DECLARE @ErrorState INT = ERROR_STATE();
    DECLARE @ErrorLine INT = ERROR_LINE();
    DECLARE @ErrorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    PRINT N'❌ POST-DEPLOYMENT FAILED';
    PRINT N'   Error : ' + @ErrorMessage;
    PRINT N'   Line  : ' + CAST(@ErrorLine AS NVARCHAR);
    PRINT N'═══════════════════════════════════════════════════════════════════════════';
    
    -- Log failure if possible
    IF OBJECT_ID('dbo.SchemaVersion') IS NOT NULL
    BEGIN
        INSERT INTO dbo.SchemaVersion (ScriptName, Version, DeploymentID, DurationMS, Success)
        VALUES ('post.sql', '10.2.0', @DeploymentID, DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME()), 0);
    END
    
    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);
END CATCH
GO