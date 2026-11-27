using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Purchase requisition completion (P2P workstream A1, migration 107,
    /// docs/PURCHASE_CYCLE_COMPLETION.md R1-R6), against real migrated
    /// SQLite databases:
    ///   * R1: a Stop budget on the Purchases account blocks submit with
    ///     ACC-5018 and records a Blocked budget-control event; a Warn
    ///     budget lets the submit through and records a BudgetOverride
    ///     workflow event plus a Warned budget-control event;
    ///   * R2: cancel works before conversion and is blocked (PUR-1002)
    ///     after conversion; close works only from Converted;
    ///   * R3: a rejected requisition can be edited and resubmitted as
    ///     approval round 2 with round 1 approvals/events intact;
    ///   * R4: PRQ -> RFQ conversion persists exactly one document link
    ///     and is idempotent on retry;
    ///   * R5/R6: required-by date, request type, department and the
    ///     warehouse selection round-trip through create/update/load.
    /// Execution requires Windows (net8.0-windows testhost).
    /// </summary>
    public class PurchaseRequisitionCompletionTests
    {
        private const string Actor = "requester";
        private const string CompanyCode = "PRQ1";

        [Fact]
        public async Task BudgetStop_BlocksSubmit_WithAcc5018_AndRecordsBlockedEvent()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                await f.SeedBudgetAsync(annual: 100m, BudgetActions.Stop, BudgetActions.Ignore);
                var id = await f.CreateRequisitionAsync(qty: 3m, price: 50m); // estimated total 150

                var error = await Assert.ThrowsAsync<AppError>(() =>
                    f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor));
                Assert.Equal(ErrorCodes.BudgetExceededAnnual, error.Code);

                // The submission rolled back: no workflow row exists.
                Assert.Null(await f.Workflow.GetAsync(f.CompanyId, id));
                Assert.Equal(1, f.BudgetEventCount("Blocked"));
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task BudgetWarn_AllowsSubmit_AndRecordsOverrideEvent()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                await f.SeedBudgetAsync(annual: 100m, BudgetActions.Warn, BudgetActions.Ignore);
                var id = await f.CreateRequisitionAsync(qty: 3m, price: 50m); // estimated total 150

                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor);

                var workflow = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.NotNull(workflow);
                Assert.Equal("Submitted", workflow!.Status);
                Assert.Contains(workflow.Events, e => e.Action == "BudgetOverride");
                Assert.Equal(1, f.BudgetEventCount("Warned"));
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task Cancel_BeforeConversion_Works_AndAfterConversion_IsBlocked()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                // Draft cancel: no workflow row exists yet.
                var draftId = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.CancelAsync(f.CompanyId, draftId, Actor, "No longer needed");
                var cancelled = await f.Workflow.GetAsync(f.CompanyId, draftId);
                Assert.Equal("Cancelled", cancelled!.Status);
                Assert.Contains(cancelled.Events, e => e.Action == "Cancelled" && e.Reason == "No longer needed");

                // Converted cancel: blocked with the catalogued code.
                var convertedId = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, convertedId, Steps("manager-1"), Actor);
                await ApproveAllAsync(f, convertedId);
                await f.Workflow.ConvertToPurchaseOrderDraftAsync(f.CompanyId, convertedId,
                    9, "Supplier Nine", "SAR", 1m, null, "buyer-1");
                var error = await Assert.ThrowsAsync<AppError>(() =>
                    f.Workflow.CancelAsync(f.CompanyId, convertedId, Actor, "Changed our mind"));
                Assert.Equal(ErrorCodes.RequisitionCancelBlocked, error.Code);

                // Close is the terminal action for a converted requisition.
                await f.Workflow.CloseAsync(f.CompanyId, convertedId, "buyer-1");
                var closed = await f.Workflow.GetAsync(f.CompanyId, convertedId);
                Assert.Equal("Closed", closed!.Status);
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task Resubmit_AfterRejection_StartsRound2_WithRound1Intact()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 2m, price: 20m);
                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor);
                var round1 = await f.Workflow.GetAsync(f.CompanyId, id);
                await f.Workflow.DecideAsync(f.CompanyId, id, round1!.Approvals[0].ApprovalId,
                    "manager-1", false, "Too expensive");
                var rejected = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal("Rejected", rejected!.Status);

                // The rejected requisition is editable again (R3).
                var snapshot = await f.Orders.FindByIdAsync(CommercialDocSide.Purchase, id);
                var edited = new CommercialDocumentDraft(
                    f.CompanyId, CommercialDocKinds.PurchaseRequisition, snapshot!.DocNo, snapshot.DocDate,
                    null, null, snapshot.WarehouseId, snapshot.CurrencyCode, snapshot.ExchangeRate,
                    snapshot.Notes,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", 1m, 15m) },
                    RequiredByDate: snapshot.RequiredByDate, RequestType: snapshot.RequestType,
                    Department: snapshot.Department);
                await f.Workflow.UpdateDraftAsync(f.CompanyId, id, edited, Actor);

                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-2"), Actor);
                var round2 = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.NotNull(round2);
                Assert.Equal("Submitted", round2!.Status);
                Assert.Equal(2, round2.RoundNo);
                Assert.Equal(2, round2.Approvals.Count);
                var first = round2.Approvals[0];
                Assert.Equal(1, first.RoundNo);
                Assert.Equal("Rejected", first.Status);
                Assert.Equal("Too expensive", first.Reason);
                var second = round2.Approvals[1];
                Assert.Equal(2, second.RoundNo);
                Assert.Equal("Pending", second.Status);
                Assert.Equal("manager-2", second.ApproverUserId);
                Assert.Contains(round2.Events, e => e.Action == "Submitted");
                Assert.Contains(round2.Events, e => e.Action == "Rejected");
                Assert.Contains(round2.Events, e => e.Action == "Resubmitted");
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task ConvertToRfq_PersistsLinkExactlyOnce_IdempotentRetry()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 2m, price: 20m);
                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor);
                await ApproveAllAsync(f, id);

                var first = await f.Workflow.ConvertToRequestForQuotationDraftAsync(f.CompanyId, id, null, "buyer-1");
                var retry = await f.Workflow.ConvertToRequestForQuotationDraftAsync(f.CompanyId, id, null, "buyer-1");
                Assert.Equal(first.RfqId, retry.RfqId);
                Assert.Equal(first.RfqNo, retry.RfqNo);

                var rfq = await f.Orders.FindByIdAsync(CommercialDocSide.Purchase, first.RfqId);
                Assert.NotNull(rfq);
                Assert.Equal(CommercialDocKinds.RequestForQuotation, rfq!.DocKind);
                Assert.Null(rfq.PartyId); // an RFQ stays supplier-neutral

                var workflow = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal("Converted", workflow!.Status);
                Assert.Equal(first.RfqId, workflow.RfqDocId);

                await using var connection = new SqliteConnection(f.ConnectionString);
                await connection.OpenAsync();
                await using var links = connection.CreateCommand();
                links.CommandText = "SELECT COUNT(*) FROM document_links WHERE company_id = $company AND source_doc_kind = 'PRQ' AND source_doc_id = $id AND target_doc_kind = 'RFQ'";
                links.Parameters.AddWithValue("$company", f.CompanyId.ToString("D"));
                links.Parameters.AddWithValue("$id", id);
                Assert.Equal(1L, Convert.ToInt64(await links.ExecuteScalarAsync()));
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task HeaderFacts_RequiredByTypeDepartmentWarehouse_RoundTrip()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m,
                    requiredBy: new DateOnly(2026, 12, 24),
                    requestType: PurchaseRequisitionRequestTypes.MaterialTransfer,
                    department: "Operations", warehouseId: 7);

                var loaded = await f.Orders.FindByIdAsync(CommercialDocSide.Purchase, id);
                Assert.NotNull(loaded);
                Assert.Equal(new DateOnly(2026, 12, 24), loaded!.RequiredByDate);
                Assert.Equal(PurchaseRequisitionRequestTypes.MaterialTransfer, loaded.RequestType);
                Assert.Equal("Operations", loaded.Department);
                Assert.Equal(7, loaded.WarehouseId);
                // Notes carry no smuggled department prefix any more (R6).
                Assert.DoesNotContain("Department:", loaded.Notes ?? string.Empty);

                var edited = new CommercialDocumentDraft(
                    f.CompanyId, CommercialDocKinds.PurchaseRequisition, loaded.DocNo, loaded.DocDate,
                    null, null, 3, loaded.CurrencyCode, loaded.ExchangeRate, loaded.Notes,
                    new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", 1m, 10m, 0m, 0m, 3) },
                    RequiredByDate: new DateOnly(2027, 1, 15),
                    RequestType: PurchaseRequisitionRequestTypes.Manufacture,
                    Department: "Production");
                await f.Workflow.UpdateDraftAsync(f.CompanyId, id, edited, Actor);

                var reloaded = await f.Orders.FindByIdAsync(CommercialDocSide.Purchase, id);
                Assert.Equal(new DateOnly(2027, 1, 15), reloaded!.RequiredByDate);
                Assert.Equal(PurchaseRequisitionRequestTypes.Manufacture, reloaded.RequestType);
                Assert.Equal("Production", reloaded.Department);
                Assert.Equal(3, reloaded.WarehouseId);
            }
            finally { f.Cleanup(); }
        }

        // ---------------------------------------------------------------
        // Permission enforcement (owner report section 11 item 6): the
        // PUR.REQ.* checks live in the service, so calling the service
        // directly without the permission fails with the catalogued
        // AUTH-2005 error — hiding the window button is not the control.
        // ---------------------------------------------------------------

        [Fact]
        public async Task Permissions_ApproveWithoutApprovePermission_RejectedWithCataloguedError()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor);
                var workflow = await f.Workflow.GetAsync(f.CompanyId, id);

                var noApprove = f.WorkflowWith(code => code != "PUR.REQ.APPROVE");
                var error = await Assert.ThrowsAsync<AppError>(() =>
                    noApprove.DecideAsync(f.CompanyId, id, workflow!.Approvals[0].ApprovalId,
                        "manager-1", true, null));
                Assert.Equal(ErrorCodes.PermissionDenied, error.Code);

                // The decision was not recorded.
                var after = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal("Pending", after!.Approvals[0].Status);
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task Permissions_ConvertWithoutConvertPermission_RejectedWithCataloguedError()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor);
                await ApproveAllAsync(f, id);

                var noConvert = f.WorkflowWith(code => code != "PUR.REQ.CONVERT");
                var poError = await Assert.ThrowsAsync<AppError>(() =>
                    noConvert.ConvertToPurchaseOrderDraftAsync(f.CompanyId, id,
                        9, "Supplier Nine", "SAR", 1m, null, "buyer-1"));
                Assert.Equal(ErrorCodes.PermissionDenied, poError.Code);
                var rfqError = await Assert.ThrowsAsync<AppError>(() =>
                    noConvert.ConvertToRequestForQuotationDraftAsync(f.CompanyId, id, null, "buyer-1"));
                Assert.Equal(ErrorCodes.PermissionDenied, rfqError.Code);

                var workflow = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal("Approved", workflow!.Status); // still unconverted
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task Permissions_SubmitCancelView_WithoutPermissions_RejectedWithCataloguedError()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);

                var noEdit = f.WorkflowWith(code => code != "PUR.REQ.EDIT");
                var submitError = await Assert.ThrowsAsync<AppError>(() =>
                    noEdit.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor));
                Assert.Equal(ErrorCodes.PermissionDenied, submitError.Code);

                var noCancel = f.WorkflowWith(code => code != "PUR.REQ.CANCEL");
                var cancelError = await Assert.ThrowsAsync<AppError>(() =>
                    noCancel.CancelAsync(f.CompanyId, id, Actor, "Not needed"));
                Assert.Equal(ErrorCodes.PermissionDenied, cancelError.Code);

                var noRights = f.WorkflowWith(_ => false);
                var viewError = await Assert.ThrowsAsync<AppError>(() =>
                    noRights.GetAsync(f.CompanyId, id));
                Assert.Equal(ErrorCodes.PermissionDenied, viewError.Code);
            }
            finally { f.Cleanup(); }
        }

        // ---------------------------------------------------------------
        // Approval scenarios (owner report section 12: "Approval
        // permissions and sequential approvals work correctly").
        // ---------------------------------------------------------------

        [Fact]
        public async Task ApprovalChain_ThreeApprovers_HappyPath()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, id,
                    Steps("manager-1", "manager-2", "manager-3"), Actor);

                var w = await f.Workflow.GetAsync(f.CompanyId, id);
                await f.Workflow.DecideAsync(f.CompanyId, id, w!.Approvals[0].ApprovalId, "manager-1", true, null);
                Assert.Equal("Submitted", (await f.Workflow.GetAsync(f.CompanyId, id))!.Status);

                w = await f.Workflow.GetAsync(f.CompanyId, id);
                await f.Workflow.DecideAsync(f.CompanyId, id, w!.Approvals[1].ApprovalId, "manager-2", true, "Looks fine");
                Assert.Equal("Submitted", (await f.Workflow.GetAsync(f.CompanyId, id))!.Status);

                w = await f.Workflow.GetAsync(f.CompanyId, id);
                await f.Workflow.DecideAsync(f.CompanyId, id, w!.Approvals[2].ApprovalId, "manager-3", true, null);
                var approved = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal("Approved", approved!.Status);
                Assert.All(approved.Approvals, a => Assert.Equal("Approved", a.Status));
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task ApprovalChain_RejectionAtStep2Of3_EndsRound_ResubmitStartsRound2()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, id,
                    Steps("manager-1", "manager-2", "manager-3"), Actor);

                var w = await f.Workflow.GetAsync(f.CompanyId, id);
                await f.Workflow.DecideAsync(f.CompanyId, id, w!.Approvals[0].ApprovalId, "manager-1", true, null);
                w = await f.Workflow.GetAsync(f.CompanyId, id);
                await f.Workflow.DecideAsync(f.CompanyId, id, w!.Approvals[1].ApprovalId, "manager-2", false, "Budget too tight");

                var rejected = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal("Rejected", rejected!.Status);
                Assert.Equal("Approved", rejected.Approvals[0].Status);
                Assert.Equal("Rejected", rejected.Approvals[1].Status);
                // The round ended at the rejection: step 3 was never decided.
                Assert.Equal("Pending", rejected.Approvals[2].Status);

                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1"), Actor);
                var round2 = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.Equal(2, round2!.RoundNo);
                Assert.Equal("Submitted", round2.Status);
                Assert.Equal(4, round2.Approvals.Count); // 3 from round 1 + 1 new
                Assert.Equal("Pending", round2.Approvals[3].Status);
                Assert.Equal(2, round2.Approvals[3].RoundNo);
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task ApprovalChain_SelfApproval_IsRejected()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                // The fixture creates drafts as Actor ("requester"), who is
                // also the document's created_by.
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps(Actor), Actor);
                var w = await f.Workflow.GetAsync(f.CompanyId, id);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    f.Workflow.DecideAsync(f.CompanyId, id, w!.Approvals[0].ApprovalId, Actor, true, null));
                Assert.Equal("Pending", (await f.Workflow.GetAsync(f.CompanyId, id))!.Approvals[0].Status);
            }
            finally { f.Cleanup(); }
        }

        [Fact]
        public async Task ApprovalChain_CannotSkipStep_OrDecideForAnotherApprover()
        {
            var f = await Fixture.CreateAsync();
            try
            {
                var id = await f.CreateRequisitionAsync(qty: 1m, price: 10m);
                await f.Workflow.SubmitAsync(f.CompanyId, id, Steps("manager-1", "manager-2"), Actor);
                var w = await f.Workflow.GetAsync(f.CompanyId, id);
                Assert.NotNull(w);
                var approvals = w!.Approvals;

                // Skipping ahead: step 2 cannot be decided while step 1 pends.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    f.Workflow.DecideAsync(f.CompanyId, id, approvals[1].ApprovalId, "manager-2", true, null));
                // Wrong user: step 1 belongs to manager-1, not manager-2.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    f.Workflow.DecideAsync(f.CompanyId, id, approvals[0].ApprovalId, "manager-2", true, null));

                // The assigned approver can still decide afterwards.
                await f.Workflow.DecideAsync(f.CompanyId, id, approvals[0].ApprovalId, "manager-1", true, null);
                Assert.Equal("Approved", (await f.Workflow.GetAsync(f.CompanyId, id))!.Approvals[0].Status);
            }
            finally { f.Cleanup(); }
        }

        private static PurchaseRequisitionApprovalStep[] Steps(params string[] approvers)
        {
            var steps = new PurchaseRequisitionApprovalStep[approvers.Length];
            for (var i = 0; i < approvers.Length; i++)
                steps[i] = new PurchaseRequisitionApprovalStep(i + 1, approvers[i]);
            return steps;
        }

        private static async Task ApproveAllAsync(Fixture f, long requisitionId)
        {
            var workflow = await f.Workflow.GetAsync(f.CompanyId, requisitionId);
            foreach (var approval in workflow!.Approvals)
                await f.Workflow.DecideAsync(f.CompanyId, requisitionId, approval.ApprovalId,
                    approval.ApproverUserId, true, null);
            var approved = await f.Workflow.GetAsync(f.CompanyId, requisitionId);
            Assert.Equal("Approved", approved!.Status);
        }

        private sealed class Fixture
        {
            public required Guid CompanyId { get; init; }
            public required string ConnectionString { get; init; }
            public required string DatabasePath { get; init; }
            public required OrderService Orders { get; init; }
            public required PurchaseRequisitionWorkflowService Workflow { get; init; }
            public required long FiscalYearId { get; init; }

            public static async Task<Fixture> CreateAsync()
            {
                var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-prq-{Guid.NewGuid():N}.sqlite3");
                var connectionString = $"Data Source={databasePath};";
                var companyId = Guid.NewGuid();

                Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", databasePath)).Success);

                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    void Exec(string sql, params (string Name, object Value)[] ps)
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = sql;
                        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                        cmd.ExecuteNonQuery();
                    }
                    Exec("INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ($id, $code, 'PRQ Test Co', 'PRQ Test Co');",
                        ("$id", companyId.ToString("D")), ("$code", CompanyCode));
                    foreach (var (code, name, type) in new[]
                    {
                        ("402", "Merchandise Purchases", "EXPENSE"),
                        ("131", "Cash on Hand", "ASSET")
                    })
                    {
                        Exec("INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted) VALUES ($id, $c, $code, $name, $type, 1, 1, 0);",
                            ("$id", Guid.NewGuid().ToString("D")), ("$c", companyId.ToString("D")),
                            ("$code", code), ("$name", name), ("$type", type));
                    }
                }

                var structure = new AccountingStructureRepository("SQLite", connectionString);
                var fiscalYearId = await structure.CreateFiscalYearAsync(companyId, 2026, Actor);

                var orders = new OrderDocumentRepository("SQLite", connectionString);
                var chain = new CommercialDocumentRepository("SQLite", connectionString);
                var orderService = new OrderService(orders, chain);
                var workflow = new PurchaseRequisitionWorkflowService(
                    new PurchaseRequisitionWorkflowRepository("SQLite", connectionString),
                    orderService, _ => true);
                return new Fixture
                {
                    CompanyId = companyId,
                    ConnectionString = connectionString,
                    DatabasePath = databasePath,
                    Orders = orderService,
                    Workflow = workflow,
                    FiscalYearId = fiscalYearId
                };
            }

            /// <summary>The same workflow service wired with a different
            /// permission predicate — the service-layer enforcement tests
            /// call the service the way any non-window caller would.</summary>
            public PurchaseRequisitionWorkflowService WorkflowWith(Func<string, bool> hasPermission)
                => new(new PurchaseRequisitionWorkflowRepository("SQLite", ConnectionString),
                    Orders, hasPermission);

            public async Task<long> CreateRequisitionAsync(decimal qty, decimal price,
                DateOnly? requiredBy = null, string? requestType = null,
                string? department = null, long? warehouseId = null)
            {
                var draft = new CommercialDocumentDraft(
                    CompanyId,
                    CommercialDocKinds.PurchaseRequisition,
                    "PRQ-" + Guid.NewGuid().ToString("N")[..8],
                    new DateOnly(2026, 11, 1),
                    null,
                    null,
                    warehouseId,
                    "SAR",
                    1m,
                    "Needed for the completion tests",
                    new[] { new CommercialLineDraft(42, "MAT-42", "Order test item", "PCS", qty, price, 0m, 0m, warehouseId) },
                    RequiredByDate: requiredBy,
                    RequestType: requestType,
                    Department: department);
                return await Workflow.CreateDraftAsync(draft, Actor);
            }

            public async Task SeedBudgetAsync(decimal annual, string actionAnnual, string actionMonthly)
            {
                var store = new JournalRepository("SQLite", ConnectionString);
                var accountId = await store.ResolveAccountIdAsync(CompanyId, "402");
                var budgets = new BudgetService(new BudgetRepository("SQLite", ConnectionString), _ => true);
                var budget = new Budget
                {
                    FiscalYearId = FiscalYearId,
                    Name = "Purchases budget",
                    BudgetAgainst = BudgetAgainstKinds.Account,
                    Status = BudgetStatuses.Draft,
                    ActionAnnual = actionAnnual,
                    ActionMonthly = actionMonthly,
                    Lines = new List<BudgetLine>
                    {
                        new()
                        {
                            AccountId = accountId,
                            AccountCode = "402",
                            AnnualAmount = annual
                        }
                    }
                };
                await budgets.SaveAsync(CompanyId, budget, Actor);
                await budgets.SetStatusAsync(CompanyId, budget.Id, BudgetStatuses.Active, Actor);
            }

            public int BudgetEventCount(string actionTaken)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM budget_control_events WHERE company_id = $company AND action_taken = $action";
                cmd.Parameters.AddWithValue("$company", CompanyId.ToString("D"));
                cmd.Parameters.AddWithValue("$action", actionTaken);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }

            public void Cleanup()
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(DatabasePath)) File.Delete(DatabasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = DatabasePath + suffix;
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
            }
        }
    }
}
