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
