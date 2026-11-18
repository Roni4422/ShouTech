using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

/// <summary>
/// Budget enforcement at posting time (Phase B design section 3,
/// migration 055). Mirrors <see cref="PartyCreditGuard"/>'s pattern: a
/// static guard called with (connection, transaction) INSIDE the posting
/// transaction — after cost-center validation/splits
/// (<see cref="GlPosting.ApplyCostCenters"/>) and before the entry is
/// flipped to Posted — from BOTH posting paths:
///   * JournalRepository.PostAsync (manual journals, JrnWin/CshWin);
///   * GlPosting.PostInTransaction (every document posting), so manual
///     journals and document postings are guarded identically.
///
/// Semantics:
///   * Only ACTIVE budgets of the entry's fiscal year are enforced, and
///     only lines whose account carries the canonical chart's budget
///     opt-in flag (chart_of_accounts.budget — the designed meaning of
///     the flag; BudgetService sets it when an account gains a budget
///     line). Unguarded accounts post exactly as before.
///   * Consumption of an (account, dimension) = Σ(debit − credit) —
///     the design's formula. CostCenter budgets read the amounts from
///     journal_line_cost_splits (a line is checked PER TARGET CENTER of
///     its applied distribution, and a budget on a parent cost center
///     covers its descendants, per the 031 hierarchy); Account budgets
///     read journal_entry_lines directly, so center-less lines count
///     in full there. A line with NO cost center is invisible to
///     CostCenter budgets (no center to attribute it to) — the
///     documented rule for cost-center-less lines.
///   * Actuals are computed from posted entries only, NET of reversals:
///     entries with status Posted (1) or Reversed (2) count, drafts (0)
///     never do. A reversed original (status 2) and its posted reversal
///     entry (status 1, swapped amounts) sum to zero, so reversing a
///     consuming posting restores its headroom exactly. Reversal
///     postings themselves are never checked (their deltas are
///     negative or offsetting by construction — and this guard is only
///     called on the forward posting paths, never from
///     ReverseInTransaction).
///   * Project budgets (decision BG3) are definable but NOT enforced in
///     v1: journal lines carry no project attribution anywhere in the
///     schema, so no posting can be charged to a project. Their lines
///     still appear in variance with budget amounts (actuals read 0 —
///     there is nothing to attribute).
///   * Per horizon (annual / accumulated monthly through the entry's
///     period), the budget's action decides: Stop → the guard throws a
///     cataloged AppError (ACC-5018 annual / ACC-5019 monthly — the
///     design proposed ACC-5009/5010, already consumed by cost-center
///     validation in Step 3) and the whole posting rolls back; Warn →
///     the posting proceeds, a budget_control_events row is written in
///     this transaction, and the warning is returned to the caller;
///     Ignore → the horizon is not checked. No override exists in v1
///     (decision BG1, the ACC-5006 precedent).
///   * Blocked events: a Stop rolls the posting transaction back, so a
///     Blocked row written inside it would vanish. The guard therefore
///     appends every violation to the caller's blockedSink before
///     throwing; the repository boundary (JournalRepository) records
///     them in budget_control_events on the same connection AFTER the
///     rollback, via <see cref="RecordBlockedEvents"/>.
/// </summary>
public static class BudgetGuard
{
    /// <summary>Runs the guard for the entry whose lines and splits are
    /// already written inside the caller's transaction. Returns the
    /// Warn-mode warnings (possibly empty). Throws AppError on the
    /// first Stop violation (after filling blockedSink with all of
    /// them).</summary>
    public static IReadOnlyList<BudgetWarning> Check(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid companyId, Guid journalEntryId, string actor,
        List<BudgetViolation>? blockedSink = null)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var isPg = prov == "PostgreSQL";
        var warnings = new List<BudgetWarning>();

        // Pre-055 database (or a fixture without the budget tables):
        // nothing is budgeted, posting proceeds exactly as before.
        if (!PartyLedger.TableExists(connection, transaction, prov, "budgets"))
            return warnings;

        // The entry's fiscal period decides the fiscal year and the
        // accumulated-monthly horizon. An entry without a period cannot
        // post anyway (period controls fire later in the same
        // transaction); the guard simply has nothing to measure against.
        long fiscalYearId;
        int periodNumber;
        using (var period = GlPosting.CreateCommand(connection, transaction,
            """
            SELECT fp.fiscal_year_id, fp.period_number
            FROM journal_entries e
            JOIN fiscal_periods fp ON fp.fiscal_period_id = e.fiscal_period_id
            WHERE e.journal_entry_id = @entry
            """))
        {
            GlPosting.Add(period, "@entry", GlPosting.FormatGuid(prov, journalEntryId));
            using var reader = period.ExecuteReader();
            if (!reader.Read())
                return warnings;
            fiscalYearId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            periodNumber = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        var budgets = LoadActiveBudgets(connection, transaction, prov, companyId, fiscalYearId);
        if (budgets.Count == 0)
            return warnings;

        var companyCode = ResolveCompanyCode(connection, transaction, prov, companyId);
        var entryLines = LoadEntryLines(connection, transaction, prov, journalEntryId);
        var flaggedAccounts = entryLines
            .Where(l => l.BudgetFlagged)
            .Select(l => l.AccountId)
            .ToHashSet();
        if (flaggedAccounts.Count == 0)
            return warnings;

        var entrySplits = LoadEntrySplits(connection, transaction, prov, journalEntryId, entryLines);
        var centerTree = LoadCostCenterTree(connection, transaction, prov, isPg, companyCode);
        var distributions = LoadDistributions(connection, transaction, prov,
            budgets.SelectMany(b => b.Lines).Select(l => l.Id).ToList());

        var violations = new List<BudgetViolation>();

        foreach (var budget in budgets)
        {
            if (budget.BudgetAgainst == BudgetAgainstKinds.Project)
                continue; // No project attribution on journal lines (v1).

            foreach (var line in budget.Lines)
            {
                if (!flaggedAccounts.Contains(line.AccountId))
                    continue;

                decimal delta;
                decimal annualBefore;
                decimal monthlyBefore;
                if (budget.BudgetAgainst == BudgetAgainstKinds.CostCenter)
                {
                    if (string.IsNullOrWhiteSpace(line.CostCenterId))
                        continue;
                    var centerSet = DescendantsOrSelf(centerTree, line.CostCenterId!);
                    delta = entrySplits
                        .Where(s => s.AccountId == line.AccountId && centerSet.Contains(s.CostCenterId))
                        .Sum(s => s.Debit - s.Credit);
                    if (delta <= 0m)
                        continue; // Credits free headroom; they never block.
                    annualBefore = ActualBySplits(connection, transaction, prov, companyId,
                        fiscalYearId, null, line.AccountId, centerSet, journalEntryId);
                    monthlyBefore = ActualBySplits(connection, transaction, prov, companyId,
                        fiscalYearId, periodNumber, line.AccountId, centerSet, journalEntryId);
                }
                else // Account
                {
                    delta = entryLines
                        .Where(l => l.AccountId == line.AccountId)
                        .Sum(l => l.Debit - l.Credit);
                    if (delta <= 0m)
                        continue;
                    annualBefore = ActualByLines(connection, transaction, prov, companyId,
                        fiscalYearId, null, line.AccountId, journalEntryId);
                    monthlyBefore = ActualByLines(connection, transaction, prov, companyId,
                        fiscalYearId, periodNumber, line.AccountId, journalEntryId);
                }

                var monthlyLimit = AccumulatedDistribution(
                    distributions.TryGetValue(line.Id, out var rows) ? rows : null,
                    line.AnnualAmount, periodNumber);

                Evaluate(budget, line, "Annual", budget.ActionAnnual,
                    line.AnnualAmount, annualBefore, delta, violations, warnings);
                Evaluate(budget, line, "Monthly", budget.ActionMonthly,
                    monthlyLimit, monthlyBefore, delta, violations, warnings);
            }
        }

        if (violations.Count > 0)
        {
            blockedSink?.AddRange(violations.Select(v => v with { JournalEntryId = journalEntryId }));
            var first = violations[0];
            var code = first.Horizon == "Annual"
                ? ErrorCodes.BudgetExceededAnnual
                : ErrorCodes.BudgetExceededMonthly;
            var message = first.Horizon == "Annual"
                ? $"Posting this entry would exceed the annual budget '{first.BudgetName}'. Limit: {first.BudgetAmount}; actual before this entry: {first.ActualBefore}; this entry: {first.EntryAmount}; overage: {first.Overage}."
                : $"Posting this entry would exceed the accumulated monthly budget '{first.BudgetName}' through this month. Limit: {first.BudgetAmount}; actual before this entry: {first.ActualBefore}; this entry: {first.EntryAmount}; overage: {first.Overage}.";
            throw new AppError(code, message,
                first.BudgetName, first.BudgetAmount, first.ActualBefore, first.EntryAmount, first.Overage);
        }

        // Warn-mode overruns are durable: one event row per warning,
        // written inside the posting transaction so it commits with the
        // posting it describes.
        foreach (var warning in warnings)
            InsertEvent(connection, transaction, prov, companyId, warning.BudgetId,
                warning.BudgetLineId, journalEntryId, "Warned", warning.Horizon,
                warning.BudgetAmount, warning.ActualBefore + warning.EntryAmount,
                warning.Overage, actor, reason: null);

        return warnings;
    }

    /// <summary>Records Blocked events for a posting the guard refused.
    /// Called by the repository boundary on the same connection AFTER
    /// the posting transaction rolled back (its own transaction).</summary>
    public static void RecordBlockedEvents(
        DbConnection connection, DbTransaction transaction, string provider,
        Guid companyId, string actor,
        IReadOnlyList<BudgetViolation> violations)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        foreach (var violation in violations)
            InsertEvent(connection, transaction, prov, companyId, violation.BudgetId,
                violation.BudgetLineId, violation.JournalEntryId, "Blocked", violation.Horizon,
                violation.BudgetAmount, violation.ActualBefore + violation.EntryAmount,
                violation.Overage, actor, violation.ErrorCode);
    }

    /// <summary>Actuals for one budget line, computed from posted
    /// entries net of reversals — the single actuals implementation the
    /// guard and the variance service share, so the numbers a user sees
    /// in BudgetVarWin are exactly the numbers the guard enforces.
    /// throughPeriod limits the monthly figure to periods 1..N of the
    /// fiscal year (accumulated monthly); the annual figure always
    /// covers the whole year.</summary>
    public static (decimal AnnualActual, decimal MonthlyActual) GetLineActuals(
        DbConnection connection, DbTransaction? transaction, string provider,
        Guid companyId, long fiscalYearId, int throughPeriod,
        BudgetLine line, string budgetAgainst)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (budgetAgainst == BudgetAgainstKinds.Project)
            return (0m, 0m); // No project attribution exists (see class doc).
        if (budgetAgainst == BudgetAgainstKinds.CostCenter)
        {
            if (string.IsNullOrWhiteSpace(line.CostCenterId))
                return (0m, 0m);
            var isPg = prov == "PostgreSQL";
            var companyCode = ResolveCompanyCode(connection, transaction, prov, companyId);
            var tree = LoadCostCenterTree(connection, transaction, prov, isPg, companyCode);
            var centerSet = DescendantsOrSelf(tree, line.CostCenterId!);
            return (
                ActualBySplits(connection, transaction, prov, companyId, fiscalYearId, null, line.AccountId, centerSet, excludeEntryId: null),
                ActualBySplits(connection, transaction, prov, companyId, fiscalYearId, throughPeriod, line.AccountId, centerSet, excludeEntryId: null));
        }
        return (
            ActualByLines(connection, transaction, prov, companyId, fiscalYearId, null, line.AccountId, excludeEntryId: null),
            ActualByLines(connection, transaction, prov, companyId, fiscalYearId, throughPeriod, line.AccountId, excludeEntryId: null));
    }

    /// <summary>The accumulated monthly limit through a period: the
    /// distribution rows summed through that period when the line has
    /// any, otherwise the equal split (annual x period / 12).</summary>
    public static decimal AccumulatedDistribution(
        IReadOnlyDictionary<int, decimal>? periodAmounts, decimal annualAmount, int throughPeriod)
    {
        if (periodAmounts is { Count: > 0 })
            return periodAmounts.Where(p => p.Key <= throughPeriod).Sum(p => p.Value);
        return annualAmount * throughPeriod / 12m;
    }

    // ------------------------------------------------------------------
    // Evaluation
    // ------------------------------------------------------------------

    private static void Evaluate(
        BudgetHeader budget, BudgetLineRow line, string horizon, string action,
        decimal limit, decimal actualBefore, decimal delta,
        List<BudgetViolation> violations, List<BudgetWarning> warnings)
    {
        if (action == BudgetActions.Ignore)
            return;
        var projected = actualBefore + delta;
        if (projected <= limit)
            return;
        var overage = projected - limit;
        if (action == BudgetActions.Stop)
        {
            violations.Add(new BudgetViolation(budget.Id, budget.Name, line.Id,
                line.AccountCode, horizon, limit, actualBefore, delta, overage,
                horizon == "Annual" ? ErrorCodes.BudgetExceededAnnual : ErrorCodes.BudgetExceededMonthly));
        }
        else // Warn
        {
            warnings.Add(new BudgetWarning(budget.Id, budget.Name, line.Id,
                line.AccountCode, horizon, limit, actualBefore, delta, overage));
        }
    }

    // ------------------------------------------------------------------
    // Actuals (posted entries net of reversals: status IN (1, 2))
    // ------------------------------------------------------------------

    private static decimal ActualByLines(
        DbConnection connection, DbTransaction? transaction, string prov,
        Guid companyId, long fiscalYearId, int? maxPeriod, Guid accountId, Guid? excludeEntryId)
    {
        var sql = """
            SELECT COALESCE(SUM(l.debit - l.credit), 0)
            FROM journal_entry_lines l
            JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            JOIN fiscal_periods fp ON fp.fiscal_period_id = e.fiscal_period_id
            WHERE e.company_id = @company AND e.status IN (1, 2)
              AND fp.fiscal_year_id = @fy AND l.account_id = @account
            """ + (maxPeriod.HasValue ? " AND fp.period_number <= @maxPeriod" : "")
                + (excludeEntryId.HasValue ? " AND e.journal_entry_id <> @exclude" : "");
        using var command = GlPosting.CreateCommand(connection, transaction, sql);
        GlPosting.Add(command, "@company", GlPosting.FormatGuid(prov, companyId));
        GlPosting.Add(command, "@fy", fiscalYearId);
        GlPosting.Add(command, "@account", GlPosting.FormatGuid(prov, accountId));
        if (maxPeriod.HasValue) GlPosting.Add(command, "@maxPeriod", maxPeriod.Value);
        if (excludeEntryId.HasValue) GlPosting.Add(command, "@exclude", GlPosting.FormatGuid(prov, excludeEntryId.Value));
        return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static decimal ActualBySplits(
        DbConnection connection, DbTransaction? transaction, string prov,
        Guid companyId, long fiscalYearId, int? maxPeriod, Guid accountId,
        IReadOnlyCollection<string> centerIds, Guid? excludeEntryId)
    {
        if (centerIds.Count == 0)
            return 0m;
        var inList = string.Join(", ", centerIds.Select((_, i) => $"@cc{i}"));
        var sql = $"""
            SELECT COALESCE(SUM(s.debit_amount - s.credit_amount), 0)
            FROM journal_line_cost_splits s
            JOIN journal_entries e ON e.journal_entry_id = s.journal_entry_id
            JOIN journal_entry_lines l ON l.journal_entry_id = s.journal_entry_id AND l.line_number = s.line_number
            JOIN fiscal_periods fp ON fp.fiscal_period_id = e.fiscal_period_id
            WHERE e.company_id = @company AND e.status IN (1, 2)
              AND fp.fiscal_year_id = @fy AND l.account_id = @account
              AND s.cost_center_id IN ({inList})
            """ + (maxPeriod.HasValue ? " AND fp.period_number <= @maxPeriod" : "")
                + (excludeEntryId.HasValue ? " AND e.journal_entry_id <> @exclude" : "");
        using var command = GlPosting.CreateCommand(connection, transaction, sql);
        GlPosting.Add(command, "@company", GlPosting.FormatGuid(prov, companyId));
        GlPosting.Add(command, "@fy", fiscalYearId);
        GlPosting.Add(command, "@account", GlPosting.FormatGuid(prov, accountId));
        var i = 0;
        foreach (var centerId in centerIds)
        {
            // Splits store the center id in the master's own type:
            // TEXT on SQLite, uuid on PostgreSQL.
            GlPosting.Add(command, $"@cc{i++}",
                prov == "PostgreSQL" ? (object)Guid.Parse(centerId) : centerId);
        }
        if (maxPeriod.HasValue) GlPosting.Add(command, "@maxPeriod", maxPeriod.Value);
        if (excludeEntryId.HasValue) GlPosting.Add(command, "@exclude", GlPosting.FormatGuid(prov, excludeEntryId.Value));
        return Convert.ToDecimal(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Loaders
    // ------------------------------------------------------------------

    private sealed record BudgetHeader(
        Guid Id, string Name, string BudgetAgainst, string ActionAnnual, string ActionMonthly,
        List<BudgetLineRow> Lines);
    private sealed record BudgetLineRow(
        Guid Id, Guid AccountId, string AccountCode, string? CostCenterId, string? ProjectId,
        decimal AnnualAmount);
    private sealed record EntryLineRow(int LineNumber, Guid AccountId, decimal Debit, decimal Credit, bool BudgetFlagged);
    private sealed record EntrySplitRow(Guid AccountId, string CostCenterId, decimal Debit, decimal Credit);

    private static List<BudgetHeader> LoadActiveBudgets(
        DbConnection connection, DbTransaction transaction, string prov,
        Guid companyId, long fiscalYearId)
    {
        var notDeleted = prov == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
        var headers = new List<BudgetHeader>();
        var byId = new Dictionary<Guid, BudgetHeader>();
        using (var command = GlPosting.CreateCommand(connection, transaction,
            $"""
            SELECT budget_id, name, budget_against, action_annual, action_monthly
            FROM budgets
            WHERE company_id = @company AND fiscal_year_id = @fy AND status = 'Active' AND {notDeleted}
            ORDER BY name
            """))
        {
            GlPosting.Add(command, "@company", GlPosting.FormatGuid(prov, companyId));
            GlPosting.Add(command, "@fy", fiscalYearId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var header = new BudgetHeader(
                    Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    new List<BudgetLineRow>());
                headers.Add(header);
                byId[header.Id] = header;
            }
        }
        if (headers.Count == 0)
            return headers;
        var inList = string.Join(", ", headers.Select((_, i) => $"@b{i}"));
        using (var command = GlPosting.CreateCommand(connection, transaction,
            $"""
            SELECT budget_id, budget_line_id, account_id, account_code, cost_center_id, project_id, annual_amount
            FROM budget_lines WHERE budget_id IN ({inList}) ORDER BY account_code
            """))
        {
            for (var i = 0; i < headers.Count; i++)
                GlPosting.Add(command, $"@b{i}", GlPosting.FormatGuid(prov, headers[i].Id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var budgetId = Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
                if (!byId.TryGetValue(budgetId, out var header))
                    continue;
                header.Lines.Add(new BudgetLineRow(
                    Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                    Guid.Parse(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)!),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture)));
            }
        }
        return headers;
    }

    private static List<EntryLineRow> LoadEntryLines(
        DbConnection connection, DbTransaction transaction, string prov, Guid journalEntryId)
    {
        // The budget opt-in flag lives on the canonical chart (059). On
        // a pre-unification database the column does not exist and no
        // account is flagged — the guard enforces nothing there.
        var hasBudgetFlag = PartyLedger.ColumnExists(connection, transaction, prov, "chart_of_accounts", "budget");
        var flagExpr = hasBudgetFlag ? "COALESCE(a.budget, 0)" : "0";
        var lines = new List<EntryLineRow>();
        using var command = GlPosting.CreateCommand(connection, transaction,
            $"""
            SELECT l.line_number, l.account_id, l.debit, l.credit, {flagExpr}
            FROM journal_entry_lines l
            LEFT JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE l.journal_entry_id = @entry ORDER BY l.line_number
            """);
        GlPosting.Add(command, "@entry", GlPosting.FormatGuid(prov, journalEntryId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(new EntryLineRow(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Guid.Parse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!),
                Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture)));
        }
        return lines;
    }

    private static List<EntrySplitRow> LoadEntrySplits(
        DbConnection connection, DbTransaction transaction, string prov,
        Guid journalEntryId, IReadOnlyList<EntryLineRow> entryLines)
    {
        // Splits are keyed by (entry, line_number); map them back to the
        // line's account through the entry's own line order.
        var splits = new List<EntrySplitRow>();
        using var command = GlPosting.CreateCommand(connection, transaction,
            """
            SELECT s.line_number, s.cost_center_id, s.debit_amount, s.credit_amount
            FROM journal_line_cost_splits s WHERE s.journal_entry_id = @entry
            """);
        GlPosting.Add(command, "@entry", GlPosting.FormatGuid(prov, journalEntryId));
        using var reader = command.ExecuteReader();
        var accountByLine = entryLines.ToDictionary(l => l.LineNumber, l => l.AccountId);
        while (reader.Read())
        {
            var lineNumber = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!accountByLine.TryGetValue(lineNumber, out var accountId))
                continue;
            splits.Add(new EntrySplitRow(accountId,
                Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!,
                Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }
        return splits;
    }

    private static Dictionary<Guid, Dictionary<int, decimal>> LoadDistributions(
        DbConnection connection, DbTransaction transaction, string prov, IReadOnlyList<Guid> lineIds)
    {
        var result = new Dictionary<Guid, Dictionary<int, decimal>>();
        if (lineIds.Count == 0)
            return result;
        var inList = string.Join(", ", lineIds.Select((_, i) => $"@line{i}"));
        using var command = GlPosting.CreateCommand(connection, transaction,
            $"SELECT budget_line_id, period_no, amount FROM budget_period_amounts WHERE budget_line_id IN ({inList})");
        for (var i = 0; i < lineIds.Count; i++)
            GlPosting.Add(command, $"@line{i}", GlPosting.FormatGuid(prov, lineIds[i]));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lineId = Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
            if (!result.TryGetValue(lineId, out var rows))
                result[lineId] = rows = new Dictionary<int, decimal>();
            rows[Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)] =
                Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static Dictionary<string, List<string>> LoadCostCenterTree(
        DbConnection connection, DbTransaction? transaction, string prov, bool isPg, string? companyCode)
    {
        // parent id -> child ids, for the company whose CODE keys the
        // master-card tables (cost_centers.company_id is the code).
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(companyCode))
            return children;
        var notDeleted = isPg ? "is_deleted = false" : "is_deleted = 0";
        using var command = GlPosting.CreateCommand(connection, transaction,
            $"SELECT id, parent_id FROM cost_centers WHERE company_id = @company AND {notDeleted}");
        GlPosting.Add(command, "@company", companyCode);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
                continue;
            var id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
            var parent = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!;
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<string>();
            list.Add(id);
        }
        return children;
    }

    private static HashSet<string> DescendantsOrSelf(
        IReadOnlyDictionary<string, List<string>> childrenByParent, string rootId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var kids))
                continue;
            foreach (var kid in kids)
                if (result.Add(kid))
                    queue.Enqueue(kid);
        }
        return result;
    }

    private static string? ResolveCompanyCode(
        DbConnection connection, DbTransaction? transaction, string prov, Guid companyId)
    {
        using var command = GlPosting.CreateCommand(connection, transaction,
            "SELECT company_code FROM companies WHERE company_id = @company");
        GlPosting.Add(command, "@company", GlPosting.FormatGuid(prov, companyId));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void InsertEvent(
        DbConnection connection, DbTransaction? transaction, string prov,
        Guid companyId, Guid budgetId, Guid budgetLineId, Guid journalEntryId,
        string actionTaken, string horizon, decimal budgetAmount, decimal actualAmount,
        decimal overage, string? actor, string? reason)
    {
        using var command = GlPosting.CreateCommand(connection, transaction,
            """
            INSERT INTO budget_control_events
                (event_id, company_id, budget_id, budget_line_id, journal_entry_id,
                 action_taken, horizon, budget_amount, actual_amount, overage_amount, actor, reason)
            VALUES (@id, @company, @budget, @line, @entry, @action, @horizon,
                    @budgetAmount, @actual, @overage, @actor, @reason)
            """);
        GlPosting.Add(command, "@id", GlPosting.FormatGuid(prov, Guid.NewGuid()));
        GlPosting.Add(command, "@company", GlPosting.FormatGuid(prov, companyId));
        GlPosting.Add(command, "@budget", GlPosting.FormatGuid(prov, budgetId));
        GlPosting.Add(command, "@line", GlPosting.FormatGuid(prov, budgetLineId));
        GlPosting.Add(command, "@entry", GlPosting.FormatGuid(prov, journalEntryId));
        GlPosting.Add(command, "@action", actionTaken);
        GlPosting.Add(command, "@horizon", horizon);
        GlPosting.Add(command, "@budgetAmount", budgetAmount);
        GlPosting.Add(command, "@actual", actualAmount);
        GlPosting.Add(command, "@overage", overage);
        GlPosting.Add(command, "@actor", (object?)actor ?? DBNull.Value);
        GlPosting.Add(command, "@reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
