using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>
/// Persistence contract for the Operations module (ribbon completion pass):
/// bank reconciliation marks, POS cash closings, outbox viewer, audit viewers,
/// fiscal roll-over, invoice compression, document repost and support tickets.
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// </summary>
public interface IOperationsRepository
{
    // ------------------------------------------------------------ Bank rec
    /// <summary>Bank GL accounts: banks.gl_account_code when the banks table
    /// exists (migration 031), otherwise canonical chart_of_accounts rows
    /// flagged allow_reconcile, otherwise postable asset accounts from
    /// chart_of_accounts (migration 059 unification).</summary>
    Task<IReadOnlyList<BankAccountOption>> GetBankAccountsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Posted journal lines for one account code, with cleared state.</summary>
    Task<IReadOnlyList<BankJournalLineRow>> GetBankJournalLinesAsync(string companyId, string accountCode, CancellationToken cancellationToken = default);

    /// <summary>Upserts marks in ONE transaction.</summary>
    Task SaveBankMarksAsync(string companyId, IReadOnlyList<BankReconciliationMark> marks, string userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the marks for the given line keys in ONE transaction.</summary>
    Task UnmarkBankLinesAsync(string companyId, IReadOnlyList<string> lineKeys, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Bank statements (063, Step 8)

    /// <summary>Creates or updates a bank statement header (one
    /// transaction). Returns the statement id.</summary>
    Task<Guid> SaveBankStatementAsync(string companyId, BankStatement statement, string userId, CancellationToken cancellationToken = default);

    /// <summary>Statements for one account, newest first.</summary>
    Task<IReadOnlyList<BankStatement>> GetBankStatementsAsync(string companyId, string accountCode, CancellationToken cancellationToken = default);

    /// <summary>One statement by id, or null.</summary>
    Task<BankStatement?> GetBankStatementAsync(string companyId, Guid statementId, CancellationToken cancellationToken = default);

    /// <summary>Adds one statement line to an OPEN statement (one transaction).</summary>
    Task<Guid> AddBankStatementLineAsync(string companyId, BankStatementLine line, string userId, CancellationToken cancellationToken = default);

    /// <summary>All lines of one statement, in date order.</summary>
    Task<IReadOnlyList<BankStatementLine>> GetBankStatementLinesAsync(string companyId, Guid statementId, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes an unmatched statement line (one transaction).</summary>
    Task RemoveBankStatementLineAsync(string companyId, Guid lineId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Matches one statement line to one book line in ONE
    /// transaction: the statement line records the book line key and the
    /// cleared mark is upserted with the statement link (063).</summary>
    Task MatchBankLineAsync(string companyId, Guid statementLineId, BankReconciliationMark mark, string userId, CancellationToken cancellationToken = default);

    /// <summary>Unmatches one statement line in ONE transaction: clears
    /// its book-line link and deletes the linked mark.</summary>
    Task UnmatchBankLineAsync(string companyId, Guid statementLineId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Closes a statement (status Open -> Closed) in one
    /// transaction. The service refuses to close while the difference
    /// is non-zero (ACC-5016); the repository re-checks Open status.</summary>
    Task CloseBankStatementAsync(string companyId, Guid statementId, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ POS closing
    /// <summary>Sales-document totals used as the expected cash for a date:
    /// non-deleted SAL documents of that business date (POS sales are SAL).</summary>
    Task<PosSalesSummary> GetPosSalesSummaryAsync(string companyId, DateOnly businessDate, CancellationToken cancellationToken = default);

    Task SavePosClosingAsync(string companyId, PosCashClosing closing, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosCashClosing>> GetPosClosingsAsync(string companyId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Outbox
    Task<IReadOnlyList<OutboundMessageRow>> GetOutboundMessagesAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Resets one FAILED outbound message to QUEUED for a real retry.</summary>
    Task RetryOutboundMessageAsync(long messageId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Audit viewers
    Task<IReadOnlyList<SecurityAuditRow>> GetSecurityAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalAuditRow>> GetJournalAuditAsync(string companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Maintenance
    /// <summary>SQLite only: VACUUM + ANALYZE. Throws NotSupportedException on PostgreSQL.</summary>
    Task VacuumAnalyzeAsync(CancellationToken cancellationToken = default);

    /// <summary>Provider integrity summary (SQLite PRAGMA integrity_check, or PostgreSQL version + table count).</summary>
    Task<string> GetIntegritySummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Read-only trial-balance integrity audit (B5): per-period posted Dr=Cr totals,
    /// unbalanced posted entries, orphan journal lines, posted entries without lines.</summary>
    Task<TrialBalanceIntegrityResult> GetTrialBalanceIntegrityAsync(string companyId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Fiscal roll-over
    Task<IReadOnlyList<FiscalYearRow>> GetFiscalYearsAsync(string companyId, CancellationToken cancellationToken = default);
    Task CreateNextFiscalYearAsync(string companyId, string userId, CancellationToken cancellationToken = default);
    Task CloseFiscalYearAsync(string companyId, long fiscalYearId, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Compression
    Task<CompressCounts> GetCompressCountsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes soft-deleted DRAFT documents + their lines in ONE
    /// transaction and writes a security_audit_log record in the same
    /// transaction. Posted documents are never touched. Returns rows deleted.</summary>
    Task<int> CompressDeletedDraftsAsync(string companyId, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Repost
    /// <summary>Stored vs line-recomputed totals for one document.
    /// table is "sales" or "purchase". Returns null when not found.</summary>
    Task<DocumentTotalsRow?> GetDocumentTotalsAsync(string companyId, string table, string docNo, CancellationToken cancellationToken = default);

    /// <summary>Writes the recomputed totals transactionally + audit record.</summary>
    Task ApplyRepostAsync(string companyId, string table, DocumentTotalsRow totals, string userId, CancellationToken cancellationToken = default);

    /// <summary>Stored vs recomputed totals for one manufacturing order
    /// (migration 034): computed total = SUM(mfg_order_lines.amount) + overhead.
    /// Returns null when the order (or the mfg schema) does not exist.</summary>
    Task<DocumentTotalsRow?> GetMfgOrderTotalsAsync(string companyId, string docNo, CancellationToken cancellationToken = default);

    /// <summary>Writes the recomputed mfg total_cost + unit_cost transactionally + audit record.</summary>
    Task ApplyMfgRepostAsync(string companyId, DocumentTotalsRow totals, string userId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------ Support tickets
    /// <summary>Creates a ticket, assigning the next per-company ticket_no inside the transaction.</summary>
    Task<SupportTicket> CreateTicketAsync(string companyId, SupportTicket ticket, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(string companyId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IPlanningRepository.cs ----
/// <summary>
/// MRP-lite planning persistence (migration 058). A run reads demand
/// (open sales-order lines + materials below reorder point), explodes it
/// through active BOMs, nets requirements against engine stock balances
/// and incoming purchase orders, and persists planned production orders
/// plus purchase suggestions. Conversions create REAL documents through
/// the existing paths (MfgOrderRepository for production,
/// OrderDocumentRepository for purchase orders) and stamp the created
/// ids back onto the planning rows in the same transaction boundary the
/// conversion service defines. Company id is the company CODE; the
/// commercial-document world is reached through the company's Guid,
/// resolved from the companies registry exactly like CRM (057) does.
/// </summary>
public interface IPlanningRepository
{
    /// <summary>Runs planning and persists the run + its rows in one
    /// transaction. Returns the persisted run with all rows.</summary>
    Task<PlanningRun> RunPlanningAsync(string companyId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlanningRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default);

    Task<PlanningRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Feasibility for one finished material + quantity, from
    /// the same explosion planning uses. Throws MFG-1002 when the
    /// material has no active BOM.</summary>
    Task<FeasibilityResult> CheckFeasibilityAsync(string companyId, string materialCode, decimal qty, CancellationToken cancellationToken = default);

    /// <summary>Converts a Planned row into a real Draft manufacturing
    /// order (BOM exploded into its lines) and stamps the order id.
    /// Throws MFG-1006 when the row is not Planned.</summary>
    Task<Guid> ConvertPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Converts a Suggested row into a real Draft purchase
    /// order through OrderDocumentRepository and stamps doc id + number.
    /// Throws MFG-1006 when the row is not Suggested.</summary>
    Task<(long DocId, string DocNo)> ConvertSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default);

    Task CancelPlannedOrderAsync(string companyId, Guid plannedOrderId, string userId, CancellationToken cancellationToken = default);

    Task CancelSuggestionAsync(string companyId, Guid suggestionId, string userId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IBomRepository.cs ----
/// <summary>
/// BOM master persistence + explosion (migration 058). Company id is the
/// company CODE (the manufacturing / materials convention). Explosion is
/// multi-level and cycle-safe: a BOM that references its own finished
/// material directly or transitively is refused with MFG-1001, both at
/// save time and at explosion time (a cycle can also be created by
/// activating a second BOM after the first was saved).
/// </summary>
public interface IBomRepository
{
    Task<IReadOnlyList<Bom>> GetAllAsync(string companyId, bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<Bom?> GetByIdAsync(string companyId, Guid bomId, CancellationToken cancellationToken = default);

    /// <summary>The version a new order should use for a material: the
    /// default active version, else the highest active version.</summary>
    Task<Bom?> GetDefaultForMaterialAsync(string companyId, string materialCode, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a BOM (header + lines) in one
    /// transaction. Line standard costs are re-snapshotted from the
    /// valuation engine's current rate for each component. Setting
    /// IsDefault clears the default flag on the material's other
    /// versions. A direct self-reference is refused with MFG-1001.</summary>
    Task SaveAsync(string companyId, Bom bom, string userId, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(string companyId, Guid bomId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Expands a BOM for a requested output quantity (in the
    /// BOM's output unit) into aggregated LEAF requirements: quantities
    /// carry scrap allowances and unit-equivalent conversion to base
    /// units. Throws MFG-1001 on a cycle, MFG-1002 when the BOM is
    /// missing or inactive.</summary>
    Task<BomExplosion> ExplodeAsync(string companyId, Guid bomId, decimal outputQty, CancellationToken cancellationToken = default);

    /// <summary>Explodes the default BOM of a material; same contract as
    /// <see cref="ExplodeAsync"/>.</summary>
    Task<BomExplosion> ExplodeForMaterialAsync(string companyId, string materialCode, decimal outputQty, CancellationToken cancellationToken = default);

    /// <summary>Materials of the company for pickers (code, name, base
    /// unit, current valuation rate from the engine balances).</summary>
    Task<IReadOnlyList<MfgMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Warehouses of the company for pickers.</summary>
    Task<IReadOnlyList<MfgWarehouseOption>> GetWarehouseOptionsAsync(string companyId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IMfgOrderRepository.cs ----
/// <summary>
/// Persistence contract for the MfgOrder module (ribbon completion pass).
/// Implemented provider-neutrally (SQLite + PostgreSQL) in Infrastructure.
/// Company id is the company code stored in mfg_orders.company_id (the same
/// convention as material_stock_movements, which posting writes to).
/// </summary>
public interface IMfgOrderRepository
{
    Task<IReadOnlyList<MfgOrder>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);

    Task<MfgOrder?> GetByDocNoAsync(string companyId, int docNo, CancellationToken cancellationToken = default);

    Task<int> GetNextDocNoAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a draft (insert or replace of header + lines) in one transaction.
    /// When DocNo is 0 the next per-company number is assigned inside the
    /// transaction. Posted/cancelled orders are never overwritten.
    /// </summary>
    Task SaveDraftAsync(string companyId, MfgOrder order, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts in one transaction: validates on-hand stock for every RAW line
    /// (SUM(qty_in - qty_out) over material_stock_movements), writes the
    /// consumption and production movements plus balance updates, computes
    /// total/unit cost, and marks the order Posted.
    /// </summary>
    Task PostAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels in one transaction. A draft is simply marked Cancelled; a
    /// posted order is reversed with opposite stock movements first.
    /// </summary>
    Task CancelAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    // ---- Staged workflow (migration 058) --------------------------------
    // Draft -> Confirmed -> InProcess -> Completed -> Closed, with WIP +
    // variance GL through GlPosting (source types MFG-ISSUE / MFG-FINISH
    // / MFG-OVHD / MFG-CLOSE). Every stage action is ONE transaction:
    // stock movements (valuation engine), GL entry, header/line actuals
    // and the mfg_production_events audit row commit or roll back
    // together. Cancel of a staged order reverses exactly what its
    // stages posted, stock and GL, symmetrically.

    /// <summary>Draft → Confirmed. No stock or GL effects; records the
    /// Confirm event. The order's lines are frozen from here on (edits
    /// require a new order).</summary>
    Task<MfgStageResult> ConfirmAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Issues the given RAW quantities (partial issues allowed,
    /// several issues per order). Each issued line is costed by the
    /// valuation engine from its source warehouse; GL Dr WIP / Cr
    /// Inventory per component. First issue moves Confirmed → InProcess.</summary>
    Task<MfgStageResult> IssueAsync(string companyId, Guid orderId, IReadOnlyList<MfgIssueLine> issues, string userId, CancellationToken cancellationToken = default);

    /// <summary>Receives finished quantity (partial receipts allowed)
    /// plus the given by-product / scrap line quantities. The receipt is
    /// valued from the order's WIP pool (accumulated cost attributable
    /// to this receipt, overhead absorbed pro-rata, by-products taking
    /// their cost share out of the pool): GL Dr Finished Goods (+ Dr
    /// by-product inventory) / Cr WIP. Reaching the planned quantity
    /// moves the order to Completed.</summary>
    Task<MfgStageResult> FinishAsync(string companyId, Guid orderId, decimal finishedQty, IReadOnlyList<MfgReceiptLine> receipts, string userId, CancellationToken cancellationToken = default);

    /// <summary>Closes an InProcess / Completed order: the remaining WIP
    /// balance is settled to the manufacturing variance account
    /// (Dr/Cr Variance vs WIP) so WIP ends at exactly zero, the standard
    /// variance is recorded, and the order becomes Closed.</summary>
    Task<MfgStageResult> CloseAsync(string companyId, Guid orderId, string userId, CancellationToken cancellationToken = default);

    /// <summary>The order's staged action history (mfg_production_events).</summary>
    Task<IReadOnlyList<MfgProductionEvent>> GetEventsAsync(string companyId, Guid orderId, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IQualityRepository.cs ----
/// <summary>Quality persistence (migration 056). Quality lives in the
/// stock world, so every method keys the company by its CODE — the key
/// materials / warehouses / stock_batches are written with — unlike
/// the journal-world repositories, which key by Guid.</summary>
public interface IQualityRepository
{
    // ---- Gate setting -------------------------------------------------
    /// <summary>The company's QC_GATE_MODE (quality_settings); the
    /// seeded default (Warn, decision Q1) when no row exists.</summary>
    Task<string> GetGateModeAsync(string companyId, CancellationToken cancellationToken = default);
    Task SetGateModeAsync(string companyId, string gateMode, string userId, CancellationToken cancellationToken = default);

    // ---- Parameters / templates ----------------------------------------
    Task<IReadOnlyList<QualityParameter>> GetParametersAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveParameterAsync(string companyId, QualityParameter parameter, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteParameterAsync(string companyId, Guid parameterId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QualityTemplate>> GetTemplatesAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveTemplateAsync(string companyId, QualityTemplate template, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteTemplateAsync(string companyId, Guid templateId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Materials with their QC wiring, for the assignment
    /// editor in QcTemplateWin.</summary>
    Task<IReadOnlyList<QualityMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Sets a material's QC wiring (check-required flags and
    /// incoming / outgoing templates) in one transaction.</summary>
    Task SetMaterialQualityAsync(string companyId, long materialId, bool qualityCheckRequired,
        bool outgoingRequired, Guid? templateInId, Guid? templateOutId, string userId,
        CancellationToken cancellationToken = default);

    // ---- Inspections ----------------------------------------------------
    /// <summary>All live inspections of the company (newest first),
    /// optionally only the pending (Draft) worklist.</summary>
    Task<IReadOnlyList<QualityInspection>> GetInspectionsAsync(string companyId, bool pendingOnly = false, CancellationToken cancellationToken = default);

    /// <summary>One inspection with its lines and readings.</summary>
    Task<QualityInspection?> GetInspectionAsync(string companyId, Guid inspectionId, CancellationToken cancellationToken = default);

    /// <summary>Creates a Draft inspection manually (the Inspect action
    /// on a document that was not gate-diverted, e.g. an outgoing
    /// inspection). Lines are snapshotted from the template when one
    /// is given (or resolved from the material's assignment).</summary>
    Task<Guid> CreateInspectionAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default);

    /// <summary>Saves a Draft inspection's line results + readings
    /// (wholesale replace inside one transaction). Terminal
    /// inspections are immutable.</summary>
    Task SaveResultsAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default);

    /// <summary>Completes a Draft inspection: evaluates any unjudged
    /// lines, writes the terminal status, moves the accepted quantity
    /// from quarantine to the destination warehouse and dispositions
    /// the rejected quantity — all through the valuation engine inside
    /// ONE transaction — and raises the failure alert.</summary>
    Task<QualityCompletionResult> CompleteAsync(string companyId, Guid inspectionId,
        decimal acceptedQty, decimal rejectedQty, string? disposition,
        string inspector, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Pending inspections whose due date has passed (the
    /// overdue worklist + the proof surface for overdue alerts).</summary>
    Task<IReadOnlyList<QualityInspection>> GetOverdueInspectionsAsync(string companyId, string asOfDate, CancellationToken cancellationToken = default);

    // ---- Alerts -----------------------------------------------------------
    Task<IReadOnlyList<QualityAlert>> GetAlertsAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default);
    Task SaveAlertAsync(string companyId, QualityAlert alert, string userId, CancellationToken cancellationToken = default);

    // ---- Reports ------------------------------------------------------------
    /// <summary>Inspection summary grouped by material, or by supplier
    /// party when <paramref name="bySupplier"/> (the reject-rate
    /// report).</summary>
    Task<IReadOnlyList<QualitySummaryRow>> GetSummaryAsync(string companyId, bool bySupplier, CancellationToken cancellationToken = default);
}

// ---- merged from src/Domain/Interfaces/IPosRepository.cs ----
/// <summary>POS session / tender / held-bill persistence (migration
/// 066, docs/BUILD_POS.md). POS lives in the party world, so the
/// company is keyed by its CODE. Document POSTING itself (GL, stock,
/// party settlement) stays in the verified InvRepo path — this
/// repository owns the till around it: sessions, tenders, held bills,
/// product search, return validation data and the Z report.</summary>
public interface IPosRepository
{
    // ---- Sessions -------------------------------------------------------
    /// <summary>Opens a session with its opening cash count. Refuses
    /// with POS-1002 when the terminal already has an open session.</summary>
    Task<PosSession> OpenSessionAsync(string companyId, string terminalCode, string cashier, int? warehouseId, string currencyCode, decimal openingCash, string userId, CancellationToken cancellationToken = default);
    Task<PosSession?> GetOpenSessionAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default);
    Task<int?> GetTerminalWarehouseIdAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default);
    Task<PosSession?> GetSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosSession>> GetSessionsAsync(string companyId, CancellationToken cancellationToken = default);
    /// <summary>Closes a session: expected cash and the variance are
    /// computed from the session's real tenders inside the closing
    /// transaction, stored on the session, mirrored to the legacy
    /// pos_cash_closings register, and returned as the Z report.</summary>
    Task<PosZReport> CloseSessionAsync(string companyId, Guid sessionId, decimal countedCash, string userId, CancellationToken cancellationToken = default);

    // ---- Settings ---------------------------------------------------------
    Task<PosSettings> GetSettingsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(string companyId, PosSettings settings, string userId, CancellationToken cancellationToken = default);

    // ---- Products -----------------------------------------------------------
    /// <summary>Contains-search over the company's materials (code,
    /// barcodes, names) for the touch grid and the search box; exact
    /// code/barcode matches sort first (the TaxSvc.ResolveMaterial
    /// ordering, generalised to a result list).</summary>
    Task<IReadOnlyList<PosProduct>> SearchProductsAsync(string companyId, string? term, int? warehouseId = null, int limit = 60, CancellationToken cancellationToken = default);

    // ---- Tenders --------------------------------------------------------------
    Task RecordTendersAsync(string companyId, IReadOnlyList<PosTender> tenders, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosTender>> GetTendersForDocAsync(string companyId, long docId, string docKind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosTender>> GetTendersForSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);

    // ---- Held bills --------------------------------------------------------------
    Task<PosHeldBill> HoldBillAsync(string companyId, PosHeldBill bill, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosHeldBill>> GetHeldBillsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<PosHeldBill?> GetHeldBillAsync(string companyId, Guid heldId, CancellationToken cancellationToken = default);
    /// <summary>Marks a held bill Recalled and returns it with lines —
    /// a bill can be recalled exactly once (POS-1009 afterwards).</summary>
    Task<PosHeldBill> RecallHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default);
    Task CancelHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default);

    // ---- Returns / Z ----------------------------------------------------------------
    /// <summary>The posted POS bill a return is taken against, with
    /// per-line already-returned quantities from earlier POS returns.</summary>
    Task<PosOriginalBill?> GetOriginalBillAsync(string companyId, string docNo, CancellationToken cancellationToken = default);
    Task<PosZReport> GetZReportAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);

    // ---- Session paid-outs (migration 104) --------------------------------------------
    /// <summary>Records a cash paid-out against an OPEN session. The
    /// amount must be positive and the reason non-blank (POS-1003
    /// otherwise); the paid-out subtracts from the session's expected
    /// cash in the Z report and at close.</summary>
    Task<PosSessionExpense> RecordSessionExpenseAsync(string companyId, Guid sessionId, decimal amount, string reason, string? referenceNo, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PosSessionExpense>> GetSessionExpensesAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default);

    // ---- Discount limits (migration 104) ------------------------------------------------
    /// <summary>The cashier's numeric discount ceiling (%): the USER
    /// row for the username wins, else the highest ROLE row among the
    /// user's roles in this company, else null (no numeric cap).</summary>
    Task<decimal?> GetDiscountLimitPctAsync(string companyId, string username, CancellationToken cancellationToken = default);
    /// <summary>Creates or replaces one limit row (subject USER =
    /// username, ROLE = role_code). The percentage must be 0–100.</summary>
    Task SaveDiscountLimitAsync(string companyId, string subjectKind, string subjectKey, decimal maxDiscountPct, string userId, CancellationToken cancellationToken = default);

    // ---- Performance (migration 104) ------------------------------------------------------
    /// <summary>Per-terminal / per-cashier performance over a period:
    /// one row per (terminal, cashier) pair that ran a session whose
    /// documents or paid-outs fall in the range; blank terminal /
    /// cashier filters match all.</summary>
    Task<IReadOnlyList<PosPerformanceRow>> GetPerformanceAsync(string companyId, DateTime from, DateTime to, string? terminalCode, string? cashier, CancellationToken cancellationToken = default);
}
