using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Payment Reconciliation persistence (SQLite + PostgreSQL).
///
/// Model: posting a sales document writes a DEBIT to party_ledger for the
/// customer; posting a purchase document writes a CREDIT for the supplier.
/// Money received from a customer is a CREDIT ledger entry; money paid to a
/// supplier is a DEBIT entry. An allocation links such a payment entry to an
/// open document and increases the document's paid_amount. Allocation runs in
/// ONE transaction and re-reads/re-validates the payment balance and the
/// document open amount inside it, so concurrent or double submissions cannot
/// over-allocate. All SQL is parameterized; the only composed identifiers are
/// the whitelisted document table names.
///
/// Step 4 discipline (sweep L-10, cash-payments MAJORs):
/// - Only genuine payment entries are allocatable: doc_kind RCV (cash
///   receipt / collected at invoice posting), PAY (cash payment) and INS
///   (installment collection). Purchase-return debits (PRT) are NOT
///   payments and can no longer be allocated as if they were cash.
/// - Sales-cycle G17 exception: a posted customer credit note (SRT) IS
///   an allocatable credit SOURCE for customers. Its allocatable amount
///   is its NET credit: the SRT credit row minus the SRT debit rows of
///   the same return document (cash refunds already paid out and
///   settlement mirrors), so refunded money can never be allocated
///   twice. It is a credit, never cash: allocation only settles other
///   open documents of the same customer in the same currency.
/// - Only real trade documents are settleable: SAL/POS on the sales side,
///   PUR on the purchase side — returns and stock kinds are excluded.
/// - No row caps: the lists are the full open sets (unlimited-rows rule);
///   settled rows are filtered in SQL, not after a LIMIT.
///
/// W8 (migration 103) additions, all inside the same allocation
/// transaction:
/// - SETTLEMENT DISCOUNTS: an allocation may settle a document by
///   payment + discount (discount in document currency, recorded on the
///   allocation row). The discount posts in BASE currency — Dr 305 Sales
///   Discounts / Cr 1201 Receivable for customers, Dr 2201 Payable /
///   Cr 405 Purchase Discounts for suppliers — in the allocation journal
///   below. Authorization (SALES.SETTLEMENT.DISCOUNT) is enforced by the
///   caller chain (window via PermServ.Require, service as backstop);
///   the repository records what it was told to post.
/// - CROSS-CURRENCY ALLOCATION: the pre-W8 hard currency block is gone.
///   `amount` stays the PAYMENT-currency amount consumed from the
///   payment (the payment list's availability math is unchanged);
///   `docAmount` is the DOCUMENT-currency amount settled on the document
///   (defaults to `amount`; required to be meaningful only when the
///   currencies differ, and forced equal when they do not). The realized
///   FX difference — settled base (docAmount x document rate) minus paid
///   base (amount x payment rate, the payment row's stored base ratio
///   first, FxRates master resolution as fallback) — posts in the SAME
///   base-currency allocation journal: for customers a positive
///   difference is a loss (Dr 509 Foreign Exchange Losses / Cr 1201) and
///   a negative one a gain (Dr 1201 / Cr 306 Other Income — no template
///   carries a dedicated FX-gain account); suppliers mirror. The signed
///   difference is stored on the allocation row (fx_amount).
/// - The allocation journal exists ONLY when a discount or an FX
///   difference is non-zero; a plain single-currency allocation writes
///   no journal, exactly as before W8.
/// - Every successful allocation writes an Allocated row to
///   sau_document_events (migration 103) in the same transaction.
/// </summary>
public sealed class ReconciliationRepository : IReconciliationRepository
{
    private const decimal Tolerance = 0.005m;

    private readonly string _provider;
    private readonly string _connectionString;

    public ReconciliationRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";

    public async Task<IReadOnlyList<OpenDocumentRow>> GetOpenDocumentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var table = DocumentTable(PartyKindToDocTable(partyKind));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var documentCompanyKey = await ResolveDocumentCompanyKeyAsync(
            connection, null, companyId, cancellationToken).ConfigureAwait(false);
        var kindFilter = PartyKindToDocTable(partyKind) == "SALES"
            ? "d.doc_kind IN ('SAL', 'POS')"
            : "d.doc_kind IN ('PUR')";
        // W8 FIFO basis: sales documents age by due_date (migration 095:
        // doc_date + party credit_days) falling back to doc_date;
        // purchase documents carry no due_date and keep doc_date order.
        var dueExpr = PartyKindToDocTable(partyKind) == "SALES"
            ? "COALESCE(d.due_date, d.doc_date)"
            : "d.doc_date";
        // Withholding (W1): a purchase invoice's open amount is
        // grand_total − withholding_amount − paid_amount; the withheld
        // part is not payable to the supplier and never allocatable.
        var hasWithholding = PartyKindToDocTable(partyKind) == "PURCHASE"
            && PartyLedger.ColumnExists(connection, null, _provider, table, "withholding_amount");
        var withholdingExpr = hasWithholding ? "COALESCE(d.withholding_amount, 0)" : "0";
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT d.doc_id, d.doc_no, d.doc_date, d.party_id, d.party_name, d.currency_code, d.grand_total, d.paid_amount,
                   COALESCE((SELECT SUM(a.amount) FROM payment_allocations a
                             WHERE a.company_id = @company AND a.doc_table = @docTable AND a.doc_id = d.doc_id
                               AND (a.document_company_id = CAST(d.company_id AS TEXT)
                                    OR (a.document_company_id IS NULL AND a.company_id = CAST(d.company_id AS TEXT)))), 0) AS allocated,
                   {dueExpr} AS due_date,
                   {withholdingExpr} AS withholding_amount
            FROM {table} d
            WHERE CAST(d.company_id AS TEXT) IN (@company, @documentCompany)
              AND d.{NotDeleted} AND d.status = 'Posted' AND d.party_id IS NOT NULL
              AND {kindFilter} AND d.grand_total - {withholdingExpr} - d.paid_amount > 0.005
            ORDER BY {dueExpr}, d.doc_id
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@documentCompany", documentCompanyKey);
        Add(command, "@docTable", PartyKindToDocTable(partyKind));
        var result = new List<OpenDocumentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new OpenDocumentRow(
                DocId: Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                DocTable: PartyKindToDocTable(partyKind),
                DocNo: ReadString(reader, 1) ?? string.Empty,
                DocDate: ReadDate(reader, 2) ?? DateOnly.FromDateTime(DateTime.Today),
                PartyId: Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                PartyName: ReadString(reader, 4),
                CurrencyCode: ReadString(reader, 5) ?? "SAR",
                Total: ReadDecimal(reader, 6),
                Paid: ReadDecimal(reader, 7),
                Allocated: ReadDecimal(reader, 8),
                DueDate: ReadDate(reader, 9),
                Withholding: ReadDecimal(reader, 10));
            if (row.OpenAmount > Tolerance)
                result.Add(row);
        }
        return result;
    }

    public async Task<IReadOnlyList<UnallocatedPaymentRow>> GetPaymentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequirePartyKind(partyKind);
        // Customers settle with credit entries; suppliers with debit entries.
        var sidePredicate = partyKind == "C" ? "l.credit > 0" : "l.debit > 0";
        var amountExpr = partyKind == "C" ? "l.credit" : "l.debit";
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Reversed vouchers remain in the append-only party ledger, but the
        // original settlement must no longer be offered as an available payment.
        var hasJournalLink = PartyLedger.ColumnExists(
            connection, null, _provider, "party_ledger", "journal_entry_id");
        var activeJournalPredicate = hasJournalLink
            ? """
              AND (l.journal_entry_id IS NULL OR NOT EXISTS (
                  SELECT 1
                  FROM journal_entries j
                  JOIN companies c
                    ON CAST(c.company_id AS TEXT) = CAST(j.company_id AS TEXT)
                  WHERE c.company_code = l.company_id
                    AND CAST(j.journal_entry_id AS TEXT) = CAST(l.journal_entry_id AS TEXT)
                    AND j.status = 2
              ))
              """
            : string.Empty;
        // G17: customer credit notes (SRT credit rows) are allocation
        // sources alongside receipts; supplier sources stay PAY/INS.
        var sourceKinds = partyKind == "C" ? "('RCV', 'INS', 'SRT')" : "('PAY', 'INS', 'ADV')";
        // Supplier advances (migration 110) appear as their own allocatable
        // source: doc_kind 'ADV' rows, flagged with their PO. Their
        // consumed part counts ONLY advance allocations (source_kind
        // 'ADVANCE'); normal payments count only 'PAYMENT' allocations —
        // the two sources never net against each other.
        var hasAdvanceLink = hasJournalLink
            && PartyLedger.TableExists(connection, null, _provider, "voucher_party_links")
            && PartyLedger.ColumnExists(connection, null, _provider, "voucher_party_links", "po_doc_id");
        var advanceJoin = hasAdvanceLink
            ? """
              LEFT JOIN voucher_party_links vl
                ON vl.journal_entry_id = CAST(l.journal_entry_id AS TEXT) AND vl.company_id = l.company_id
              LEFT JOIN purchase_documents po
                ON po.doc_id = vl.po_doc_id
              """
            : string.Empty;
        await using var command = CreateCommand(connection, null,
            $"""
            SELECT l.ledger_id, l.entry_date, l.doc_kind, l.doc_no, l.party_id, l.note, {amountExpr} AS amount,
                   COALESCE((SELECT SUM(a.amount) FROM payment_allocations a
                             WHERE a.company_id = l.company_id AND a.payment_ledger_id = l.ledger_id
                               AND COALESCE(a.source_kind, 'PAYMENT') = CASE WHEN l.doc_kind = 'ADV' THEN 'ADVANCE' ELSE 'PAYMENT' END), 0) AS allocated,
                   COALESCE(NULLIF(p.name_en, ''), p.name_ar, '') AS party_name,
                   COALESCE(l.currency_code, 'SAR') AS currency_code,
                   CASE WHEN l.doc_kind = 'SRT' THEN COALESCE((SELECT SUM(r.debit) FROM party_ledger r
                             WHERE r.company_id = l.company_id AND r.party_kind = l.party_kind
                               AND r.party_id = l.party_id AND r.doc_kind = 'SRT'
                               AND r.doc_id = l.doc_id AND r.debit > 0), 0) ELSE 0 END AS refunded
                   {(hasAdvanceLink ? ", vl.po_doc_id, po.doc_no" : ", NULL, NULL")}
            FROM party_ledger l
            LEFT JOIN parties p
              ON p.company_id = l.company_id AND p.kind = l.party_kind AND p.ledger_id = l.party_id
            {advanceJoin}
            WHERE l.company_id = @company AND l.party_kind = @kind AND {sidePredicate}
              AND l.doc_kind IN {sourceKinds}
              {activeJournalPredicate}
            ORDER BY l.entry_date, l.ledger_id
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", partyKind);
        var result = new List<UnallocatedPaymentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new UnallocatedPaymentRow(
                LedgerId: Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                EntryDate: ReadDate(reader, 1) ?? DateOnly.FromDateTime(DateTime.Today),
                DocKind: ReadString(reader, 2),
                DocNo: ReadString(reader, 3),
                PartyId: Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Note: ReadString(reader, 5),
                // G17: a credit note's allocatable amount is its net
                // credit (gross credit minus refunds/mirrors on the
                // same return document, read as column 10).
                Amount: ReadDecimal(reader, 6) - ReadDecimal(reader, 10),
                Allocated: ReadDecimal(reader, 7),
                PartyName: ReadString(reader, 8),
                CurrencyCode: ReadString(reader, 9) ?? "SAR",
                IsAdvance: string.Equals(ReadString(reader, 2), "ADV", StringComparison.OrdinalIgnoreCase),
                PoDocId: reader.IsDBNull(11) ? null : Convert.ToInt64(reader.GetValue(11), CultureInfo.InvariantCulture),
                PoDocNo: ReadString(reader, 12));
            if (row.Available > Tolerance)
                result.Add(row);
        }
        return result;
    }

    public async Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, decimal discountAmount = 0m, decimal? docAmount = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequirePartyKind(partyKind);
        var expectedDocTable = PartyKindToDocTable(partyKind);
        if (!string.Equals(docTable, expectedDocTable, StringComparison.Ordinal))
            throw new InvalidOperationException($"Documents of table '{docTable}' cannot be reconciled against party kind '{partyKind}'.");
        if (amount <= 0)
            throw new InvalidOperationException("Allocation amount must be greater than zero.");
        if (discountAmount < 0)
            throw new InvalidOperationException("Settlement discount cannot be negative.");
        if (docAmount is <= 0)
            throw new InvalidOperationException("Document allocation amount must be greater than zero.");
        var table = DocumentTable(docTable);
        var invoiceKindFilter = docTable == "SALES" ? "doc_kind IN ('SAL', 'POS')" : "doc_kind = 'PUR'";

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var documentCompanyKey = await ResolveDocumentCompanyKeyAsync(
                connection, transaction, companyId, cancellationToken).ConfigureAwait(false);

            // 1) Re-read the payment inside the transaction.
            string paymentKind;
            long paymentParty;
            decimal paymentAmount;
            string? paymentDocKind;
            string paymentCurrency;
            DateOnly paymentDate = DateOnly.FromDateTime(DateTime.Today);
            string? paymentJournalId = null;
            decimal paymentRate = 0m;
            // A reversed voucher remains in the append-only party ledger, but it is
            // no longer a valid source of funds. Enforce the same journal-status
            // guard used by GetPaymentsAsync here as well, so direct repository
            // callers cannot bypass the filtered payment list.
            var hasJournalLink = PartyLedger.ColumnExists(
                connection, transaction, _provider, "party_ledger", "journal_entry_id");
            var activeJournalPredicate = hasJournalLink
                ? """
                  AND (l.journal_entry_id IS NULL OR NOT EXISTS (
                      SELECT 1
                      FROM journal_entries j
                      JOIN companies c
                        ON CAST(c.company_id AS TEXT) = CAST(j.company_id AS TEXT)
                      WHERE c.company_code = l.company_id
                        AND CAST(j.journal_entry_id AS TEXT) = CAST(l.journal_entry_id AS TEXT)
                        AND j.status = 2
                  ))
                  """
                : string.Empty;
            long? paymentDocId = null;
            // W8: the payment's date + stored rate/base (migration 062)
            // price the realized-FX leg of a cross-currency allocation.
            var paymentStoredRate = 0m;
            decimal? paymentBaseSide = null;
            var hasBaseAmounts = PartyLedger.ColumnExists(
                connection, transaction, _provider, "party_ledger", "base_debit");
            var paymentBaseSelect = hasBaseAmounts ? ", l.base_debit, l.base_credit" : ", NULL, NULL";
            var paymentRateSelect = PartyLedger.ColumnExists(
                connection, transaction, _provider, "party_ledger", "exchange_rate")
                ? "COALESCE(l.exchange_rate, 0)" : "0";
            await using (var pay = CreateCommand(connection, transaction,
                $"""
                SELECT l.party_kind, l.party_id, l.debit, l.credit, l.doc_kind,
                       COALESCE(l.currency_code, 'SAR'), l.doc_id, l.entry_date,
                       {paymentRateSelect}{paymentBaseSelect}
                       {(hasJournalLink ? ", l.journal_entry_id" : ", NULL")}
                FROM party_ledger l
                WHERE l.company_id = @company AND l.ledger_id = @id
                  {activeJournalPredicate}
                """ + (_provider == "PostgreSQL" ? " FOR UPDATE" : "")))
            {
                Add(pay, "@company", companyId.Trim());
                Add(pay, "@id", paymentLedgerId);
                await using var reader = await pay.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The payment entry was not found for this company.");
                paymentKind = ReadString(reader, 0) ?? string.Empty;
                paymentParty = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                var debit = ReadDecimal(reader, 2);
                var credit = ReadDecimal(reader, 3);
                paymentAmount = partyKind == "C" ? credit : debit;
                paymentDocKind = ReadString(reader, 4);
                paymentCurrency = ReadString(reader, 5) ?? "SAR";
                paymentDocId = reader.IsDBNull(6) ? null : Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture);
                paymentDate = ReadDate(reader, 7) ?? paymentDate;
                paymentStoredRate = ReadDecimal(reader, 8);
                paymentRate = paymentStoredRate;
                if (hasBaseAmounts)
                {
                    var baseSide = partyKind == "C" ? ReadDecimal(reader, 10) : ReadDecimal(reader, 9);
                    paymentBaseSide = baseSide > 0 ? baseSide : null;
                }
                paymentJournalId = ReadString(reader, 11);
            }
            // Supplier advances (migration 110) are allocatable ONLY as
            // advances: an 'ADV' entry takes the advance branch below
            // (same-PO enforcement + Dr AP / Cr Advances clearing).
            var isAdvance = string.Equals(paymentDocKind, "ADV", StringComparison.OrdinalIgnoreCase);
            // Step 4 (L-10): purchase returns and other non-payment
            // entries are not allocatable payments, even if they sit on
            // the payment side. G17: a customer credit note (SRT) is the
            // one return that IS an allocation source — for customers
            // only, and only up to its NET credit (gross credit minus
            // the refunds/settlement mirrors already paid out on the
            // same return document, which sit as SRT debit rows).
            var isCreditNote = paymentDocKind == "SRT" && partyKind == "C";
            if (!isAdvance && paymentDocKind is not ("RCV" or "PAY" or "INS") && !isCreditNote)
                throw new InvalidOperationException("The selected ledger entry is not a payment (only receipt, payment, installment-collection and customer credit-note entries can be allocated).");
            if (isCreditNote)
            {
                if (paymentDocId is not long creditDocId)
                    throw new InvalidOperationException("The credit note entry is not linked to its return document and cannot be allocated.");
                await using var refunds = CreateCommand(connection, transaction,
                    """
                    SELECT COALESCE(SUM(debit), 0) FROM party_ledger
                    WHERE company_id = @company AND party_kind = @kind AND party_id = @party
                      AND doc_kind = 'SRT' AND doc_id = @doc AND debit > 0
                    """);
                Add(refunds, "@company", companyId.Trim());
                Add(refunds, "@kind", partyKind);
                Add(refunds, "@party", paymentParty);
                Add(refunds, "@doc", creditDocId);
                var refunded = Convert.ToDecimal(await refunds.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                paymentAmount -= refunded;
                if (paymentAmount <= 0)
                    throw new InvalidOperationException("The credit note has no remaining credit (it was fully refunded in cash).");
            }
            if (isAdvance && partyKind != "S")
                throw new InvalidOperationException("Supplier advances can only be allocated against supplier documents.");
            if (!string.Equals(paymentKind, partyKind, StringComparison.Ordinal))
                throw new InvalidOperationException("The payment entry belongs to a different party kind.");
            if (paymentAmount <= 0)
                throw new InvalidOperationException("The selected ledger entry is not a payment on the expected side.");

            decimal paymentAllocated;
            await using (var sum = CreateCommand(connection, transaction,
                "SELECT COALESCE(SUM(amount), 0) FROM payment_allocations WHERE company_id = @company AND payment_ledger_id = @id AND COALESCE(source_kind, 'PAYMENT') = @sourceKind"))
            {
                Add(sum, "@company", companyId.Trim());
                Add(sum, "@id", paymentLedgerId);
                Add(sum, "@sourceKind", isAdvance ? "ADVANCE" : "PAYMENT");
                paymentAllocated = Convert.ToDecimal(await sum.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            if (amount > paymentAmount - paymentAllocated + Tolerance)
                throw new InvalidOperationException("Allocation exceeds the unallocated remainder of this payment.");

            // 2) Re-read the document inside the transaction.
            string docNo;
            string actualDocumentCompanyKey;
            long docParty;
            decimal openAmount;
            decimal docTotal, docPaid, docWithholding = 0m;
            string docStatus;
            string documentCurrency;
            string docKind = string.Empty;
            var docStoredRate = 0m;
            var hasDocWithholding = docTable == "PURCHASE"
                && PartyLedger.ColumnExists(connection, transaction, _provider, table, "withholding_amount");
            await using (var doc = CreateCommand(connection, transaction,
                $"SELECT CAST(company_id AS TEXT), doc_no, party_id, grand_total, paid_amount, status, COALESCE(currency_code, 'SAR'), doc_kind, COALESCE(exchange_rate, 0){(hasDocWithholding ? ", COALESCE(withholding_amount, 0)" : "")} FROM {table} WHERE CAST(company_id AS TEXT) IN (@company, @documentCompany) AND doc_id = @doc AND {invoiceKindFilter} AND {NotDeleted}"
                + (_provider == "PostgreSQL" ? " FOR UPDATE" : "")))
            {
                Add(doc, "@company", companyId.Trim());
                Add(doc, "@documentCompany", documentCompanyKey);
                Add(doc, "@doc", docId);
                await using var reader = await doc.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The document was not found for this company.");
                actualDocumentCompanyKey = ReadString(reader, 0) ?? string.Empty;
                docNo = ReadString(reader, 1) ?? string.Empty;
                docParty = reader.IsDBNull(2) ? -1 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                docTotal = ReadDecimal(reader, 3);
                docPaid = ReadDecimal(reader, 4);
                docStatus = ReadString(reader, 5) ?? string.Empty;
                documentCurrency = ReadString(reader, 6) ?? "SAR";
                docKind = ReadString(reader, 7) ?? string.Empty;
                docStoredRate = ReadDecimal(reader, 8);
                if (hasDocWithholding) docWithholding = ReadDecimal(reader, 9);
            }
            if (!string.Equals(docStatus, "Posted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only posted documents can be reconciled.");
            // paid_amount already includes every prior allocation (this same
            // transaction maintains it), so it is the document's settled total.
            // Withholding (W1): the withheld part is not payable to the
            // supplier — allocation settles against the NET open amount,
            // so paying the net fully settles the invoice and any amount
            // beyond the net is rejected by the open-amount check below.
            // This also bounds advance consumption: an advance allocated
            // here can never consume the withheld portion.
            openAmount = docTotal - docWithholding - docPaid;
            if (docParty != paymentParty)
                throw new InvalidOperationException("The payment and the document belong to different parties.");
            // W8: cross-currency allocation is ALLOWED (the pre-W8 hard
            // block is removed). `amount` is consumed from the payment in
            // payment currency; `settledDoc` settles the document in
            // document currency. Same currency: the two must agree, so
            // single-currency behavior is byte-identical to before.
            var crossCurrency = !string.Equals(paymentCurrency.Trim(), documentCurrency.Trim(), StringComparison.OrdinalIgnoreCase);
            if (crossCurrency && docAmount is null)
                throw new InvalidOperationException(
                    "A document-currency amount is required for cross-currency allocations.");
            var settledDoc = docAmount ?? amount;
            if (!crossCurrency && settledDoc != amount)
                throw new InvalidOperationException(
                    "The document amount must equal the payment amount when both share a currency.");
            if (settledDoc + discountAmount > openAmount + Tolerance)
                throw new InvalidOperationException("Allocation plus settlement discount exceeds the document's open amount.");

            // Advance consumption respects the PO (Y1): the advance's PO
            // is read from its voucher link, and the invoice's PO ancestry
            // is walked through document_links (PO -> invoice directly,
            // or PO -> receipt -> invoice). Any other invoice — another
            // PO's, or a manual invoice with no PO at all — is rejected
            // with the catalogued PUR-1012 and nothing is written.
            long? advancePoDocId = null;
            if (isAdvance)
            {
                if (paymentJournalId is not null
                    && PartyLedger.TableExists(connection, transaction, _provider, "voucher_party_links")
                    && PartyLedger.ColumnExists(connection, transaction, _provider, "voucher_party_links", "po_doc_id"))
                {
                    await using var link = CreateCommand(connection, transaction,
                        "SELECT po_doc_id FROM voucher_party_links WHERE journal_entry_id = @journal AND company_id = @company AND "
                        + (_provider == "PostgreSQL" ? "is_advance = TRUE" : "is_advance = 1"));
                    Add(link, "@journal", paymentJournalId);
                    Add(link, "@company", companyId.Trim());
                    var linkValue = await link.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (linkValue is not null and not DBNull)
                        advancePoDocId = Convert.ToInt64(linkValue, CultureInfo.InvariantCulture);
                }
                var invoicePoIds = new HashSet<long>();
                if (PartyLedger.TableExists(connection, transaction, _provider, "document_links"))
                {
                    await using var chain = CreateCommand(connection, transaction, """
                        WITH RECURSIVE chain (doc_id, doc_kind) AS (
                            SELECT source_doc_id, source_doc_kind FROM document_links
                            WHERE company_id = @docCompany AND target_doc_id = @doc AND target_doc_kind = 'PUR'
                            UNION
                            SELECT l.source_doc_id, l.source_doc_kind FROM document_links l
                            JOIN chain c ON l.target_doc_id = c.doc_id AND l.target_doc_kind = c.doc_kind
                            WHERE l.company_id = @docCompany
                        )
                        SELECT doc_id FROM chain WHERE doc_kind = 'PO'
                        """);
                    Add(chain, "@docCompany", documentCompanyKey);
                    Add(chain, "@doc", docId);
                    await using var chainReader = await chain.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await chainReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        invoicePoIds.Add(Convert.ToInt64(chainReader.GetValue(0), CultureInfo.InvariantCulture));
                }
                if (advancePoDocId is null || !invoicePoIds.Contains(advancePoDocId.Value))
                    throw new AppError(ErrorCodes.AdvancePurchaseOrderMismatch,
                        $"Supplier advance for purchase order '{advancePoDocId?.ToString(CultureInfo.InvariantCulture) ?? "?"}' cannot be allocated to invoice '{docNo}', which belongs to a different purchase order.",
                        docNo);
            }

            // An enabled company policy fails closed: a supplier invoice without
            // a current evaluation cannot be paid through reconciliation.
            // A disabled policy preserves the company's pre-control behavior.
            if (docTable == "PURCHASE")
            {
                var policyEnabled = false;
                var currentPolicyVersion = 0;
                var policySql = """
                    SELECT is_enabled, policy_version
                    FROM purchase_match_policies
                    WHERE company_code = @company
                    """ + (_provider == "PostgreSQL" ? " FOR SHARE" : string.Empty);
                await using (var policy = CreateCommand(connection, transaction, policySql))
                {
                    Add(policy, "@company", companyId.Trim());
                    await using var reader = await policy.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        policyEnabled = Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
                        currentPolicyVersion = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                    }
                }

                if (policyEnabled)
                {
                    string? evaluationId = null;
                    string? decision = null;
                    var evaluationPolicyVersion = 0;
                    await using (var evaluation = CreateCommand(connection, transaction, """
                        SELECT evaluation_id, decision, policy_version
                        FROM purchase_match_evaluations
                        WHERE company_code = @company AND invoice_doc_id = @doc
                        ORDER BY evaluation_version DESC
                        LIMIT 1
                        """))
                    {
                        Add(evaluation, "@company", companyId.Trim());
                        Add(evaluation, "@doc", docId);
                        await using var reader = await evaluation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            evaluationId = ReadString(reader, 0);
                            decision = ReadString(reader, 1);
                            evaluationPolicyVersion = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(evaluationId))
                        throw new InvalidOperationException(
                            "This supplier invoice has no three-way match evaluation. Evaluate it against a posted purchase order and goods receipt before allocating payment.");

                    if (evaluationPolicyVersion != currentPolicyVersion)
                        throw new InvalidOperationException(
                            "The purchase match policy changed after this invoice was evaluated. Re-evaluate the invoice under the current policy before allocating payment.");

                    if (string.Equals(decision, "Blocked", StringComparison.Ordinal))
                    {
                        await using var release = CreateCommand(connection, transaction, """
                            SELECT 1
                            FROM purchase_match_overrides
                            WHERE company_code = @company
                              AND invoice_doc_id = @doc
                              AND evaluation_id = @evaluation
                            LIMIT 1
                            """);
                        Add(release, "@company", companyId.Trim());
                        Add(release, "@doc", docId);
                        Add(release, "@evaluation", evaluationId);
                        var hasRelease = await release.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                        if (hasRelease is null or DBNull)
                            throw new InvalidOperationException(
                                "This supplier invoice is blocked by the purchase three-way match control and requires an authorized, audited release before payment allocation.");
                    }
                    else if (!string.Equals(decision, "Pass", StringComparison.Ordinal) &&
                             !string.Equals(decision, "Warning", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The supplier invoice has an invalid three-way match decision and cannot be paid until it is re-evaluated.");
                    }
                }
            }

            // The allocation id is minted before the journal so the
            // journal's source link (ALLOC / allocation id) is stable.
            var allocationIdForJournal = Guid.NewGuid();

            // Advance clearing (migration 110), inside THIS allocation
            // transaction: Dr AP control 2201 (the invoice liability is
            // settled by the prepayment) / Cr Advances 1215 (the advance
            // is consumed). The entry id is stored on the allocation so
            // unallocation can mirror exactly this entry.
            string? clearingJournalId = null;
            if (isAdvance)
            {
                var clearingCompanyGuid = await ResolveCompanyGuidAsync(
                    connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
                var clearing = GlPosting.PostInTransaction(connection, transaction, _provider,
                    new GlDocumentRequest(
                        clearingCompanyGuid, paymentDate,
                        $"Supplier advance applied to invoice {docNo}",
                        "ADV_ALLOC", allocationIdForJournal.ToString("D"), docNo,
                        paymentCurrency, paymentRate,
                        new[]
                        {
                            new GlLineRequest(GlAccounts.Payable, amount, 0m),
                            new GlLineRequest(GlAccounts.SupplierAdvances, 0m, amount)
                        }),
                    userId);
                clearingJournalId = clearing.JournalEntryId.ToString("D");
            }

            // 3) Price the settlement in base currency (W8). The document
            // rate resolves through the ONE FxRates path (document rate,
            // else currency_rates master, else ACC-5015). The payment rate
            // is the rate the payment was BOOKED at: its stored base ratio
            // when migration 062 amounts exist, else the same FxRates
            // resolution over the payment's own date/rate.
            // The allocation journal is dated on the PAYMENT's entry
            // date: the realized FX is priced at the rates the money was
            // booked at, and the entry lands in the payment's own fiscal
            // period instead of failing when "today" sits in a period
            // the receipt never belonged to.
            var allocationDate = paymentDate;
            var fxBase = 0m;
            var discountBase = 0m;
            if (discountAmount > 0 || crossCurrency)
            {
                // The GL world keys by company Guid: the resolved
                // document company key (legacy document rows may store
                // the company CODE in company_id, which is not a Guid).
                var companyGuid = Guid.Parse(documentCompanyKey);
                var docFx = FxRates.Resolve(connection, transaction, _provider,
                    companyGuid, documentCurrency, allocationDate, docStoredRate);
                discountBase = discountAmount > 0 ? FxRates.ToBase(discountAmount, docFx.Rate) : 0m;
                if (crossCurrency)
                {
                    decimal payRate;
                    if (paymentBaseSide is decimal storedBase && paymentAmount > 0)
                        payRate = storedBase / paymentAmount;
                    else
                        payRate = FxRates.ResolveByCompanyCode(connection, transaction, _provider,
                            companyId.Trim(), paymentCurrency, paymentDate, paymentStoredRate).Rate;
                    fxBase = FxRates.ToBase(settledDoc, docFx.Rate) - FxRates.ToBase(amount, payRate);
                }

                // The allocation journal (base currency, rate 1): the
                // discount leg and the realized-FX leg. The receipt
                // voucher already credited the control account for the
                // cash; these legs credit it for the discount/FX part of
                // the settlement, so control == settled total exactly.
                var lines = new List<GlLineRequest>();
                var controlAccount = partyKind == "C" ? GlAccounts.Receivable : GlAccounts.Payable;
                if (discountBase > 0)
                {
                    if (partyKind == "C")
                    {
                        lines.Add(new GlLineRequest(GlAccounts.SalesDiscounts, discountBase, 0m));
                        lines.Add(new GlLineRequest(GlAccounts.Receivable, 0m, discountBase));
                    }
                    else
                    {
                        lines.Add(new GlLineRequest(GlAccounts.Payable, discountBase, 0m));
                        lines.Add(new GlLineRequest(GlAccounts.PurchaseDiscounts, 0m, discountBase));
                    }
                }
                if (Math.Abs(fxBase) > Tolerance)
                {
                    // Customers: settled base above paid base is a LOSS.
                    // Suppliers mirror: settling more base than was paid
                    // out is a GAIN.
                    var isLoss = partyKind == "C" ? fxBase > 0 : fxBase < 0;
                    var magnitude = Math.Abs(fxBase);
                    if (isLoss)
                    {
                        lines.Add(new GlLineRequest(GlAccounts.ForeignExchangeLosses, magnitude, 0m));
                        lines.Add(new GlLineRequest(controlAccount, 0m, magnitude));
                    }
                    else
                    {
                        lines.Add(new GlLineRequest(controlAccount, magnitude, 0m));
                        lines.Add(new GlLineRequest(GlAccounts.OtherIncome, 0m, magnitude));
                    }
                }
                if (lines.Count >= 2)
                {
                    var baseCurrency = FxRates.ResolveBaseCurrency(connection, transaction, _provider, companyGuid);
                    GlPosting.PostInTransaction(connection, transaction, _provider,
                        new GlDocumentRequest(companyGuid, allocationDate,
                            $"Allocation {docNo}: payment {amount:0.####} {paymentCurrency.Trim()}, settled {settledDoc:0.####} {documentCurrency.Trim()}, discount {discountAmount:0.####}, FX {fxBase:0.####}",
                            "ALLOC", allocationIdForJournal.ToString("D"), docNo,
                            baseCurrency ?? documentCurrency.Trim(), 1m, lines),
                        userId);
                }
            }

            // 4) Insert the allocation and settle it against the document.
            await using (var insert = CreateCommand(connection, transaction,
                "INSERT INTO payment_allocations (allocation_id, company_id, document_company_id, party_kind, party_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, allocated_by, note, discount_amount, doc_amount, fx_amount, source_kind, advance_po_doc_id, clearing_journal_id) VALUES (@id, @company, @documentCompany, @kind, @party, @payment, @docTable, @doc, @docNo, @amount, @user, @note, @discount, @docAmount, @fx, @sourceKind, @advancePo, @clearing)"))
            {
                Add(insert, "@id", FormatGuid(allocationIdForJournal));
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@documentCompany", actualDocumentCompanyKey);
                Add(insert, "@kind", partyKind);
                Add(insert, "@party", paymentParty);
                Add(insert, "@payment", paymentLedgerId);
                Add(insert, "@docTable", docTable);
                Add(insert, "@doc", docId);
                Add(insert, "@docNo", docNo);
                Add(insert, "@amount", amount);
                Add(insert, "@user", userId);
                Add(insert, "@note", (object?)NullIfBlank(note) ?? DBNull.Value);
                Add(insert, "@discount", discountAmount);
                Add(insert, "@docAmount", crossCurrency ? (object)settledDoc : DBNull.Value);
                Add(insert, "@fx", fxBase);
                Add(insert, "@sourceKind", isAdvance ? "ADVANCE" : "PAYMENT");
                Add(insert, "@advancePo", (object?)advancePoDocId ?? DBNull.Value);
                Add(insert, "@clearing", (object?)clearingJournalId ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var settle = CreateCommand(connection, transaction,
                $"UPDATE {table} SET paid_amount = paid_amount + @amount WHERE CAST(company_id AS TEXT) = @documentCompany AND doc_id = @doc AND {NotDeleted}"))
            {
                Add(settle, "@amount", settledDoc + discountAmount);
                Add(settle, "@documentCompany", actualDocumentCompanyKey);
                Add(settle, "@doc", docId);
                if (await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("The document could not be settled (missing or deleted).");
            }

            // W8: the unified document event, inside the same transaction
            // (company keyed by the resolved Guid, like the chain events).
            DocEvents.Write(connection, transaction, _provider,
                documentCompanyKey, docTable, docKind, docId, docNo,
                DocEventTypes.Allocated, userId,
                $"payment_ledger_id={paymentLedgerId}; amount={amount.ToString(CultureInfo.InvariantCulture)} {paymentCurrency.Trim()}; doc_amount={settledDoc.ToString(CultureInfo.InvariantCulture)} {documentCurrency.Trim()}; discount={discountAmount.ToString(CultureInfo.InvariantCulture)}; fx_base={fxBase.ToString(CultureInfo.InvariantCulture)}");

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return allocationIdForJournal;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reverses ONE advance allocation (migration 110) in one transaction:
    /// the allocation's clearing journal (Dr AP / Cr Advances) is mirrored
    /// by the shared reversal core, the invoice's paid_amount is released,
    /// a migration-079 audit row preserves the allocation's provenance,
    /// and the allocation row is deleted — which is exactly what restores
    /// the advance's available balance. Normal payment allocations are
    /// NOT unallocated here (they release only with their voucher's
    /// reversal, the pre-095 discipline).
    /// Returns the clearing entry's reversal journal id.
    /// </summary>
    public async Task<Guid> UnallocateAsync(string companyId, Guid allocationId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (allocationId == Guid.Empty)
            throw new ArgumentException("An allocation is required.", nameof(allocationId));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A current user is required for audited writes.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reversalId = await UnallocateCoreAsync(connection, transaction, companyId, allocationId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reversalId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reverses the most recent advance allocation between one
    /// payment/advance entry and one document (the PayRecWin action).</summary>
    public async Task<Guid> UnallocateLatestAdvanceAsync(string companyId, long paymentLedgerId, string docTable, long docId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid allocationId;
            await using (var find = CreateCommand(connection, transaction,
                "SELECT allocation_id FROM payment_allocations WHERE company_id = @company AND payment_ledger_id = @payment AND doc_table = @docTable AND doc_id = @doc AND COALESCE(source_kind, 'PAYMENT') = 'ADVANCE' ORDER BY allocated_at DESC, allocation_id DESC LIMIT 1"))
            {
                Add(find, "@company", companyId.Trim());
                Add(find, "@payment", paymentLedgerId);
                Add(find, "@docTable", docTable);
                Add(find, "@doc", docId);
                var value = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is null or DBNull)
                    throw new InvalidOperationException("No advance allocation exists between the selected payment and document.");
                allocationId = Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            }
            var reversalId = await UnallocateCoreAsync(connection, transaction, companyId, allocationId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reversalId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Guid> UnallocateCoreAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid allocationId, string userId, CancellationToken cancellationToken)
    {
        string allocCompany, allocDocTable, allocDocNo, sourceKind;
        long allocPayment, allocDoc;
        decimal allocAmount;
        string? allocDocumentCompany, allocAllocatedBy, allocNote, clearingJournalId;
        await using (var read = CreateCommand(connection, transaction,
            "SELECT company_id, payment_ledger_id, doc_table, doc_id, doc_no, amount, document_company_id, allocated_by, note, COALESCE(source_kind, 'PAYMENT'), clearing_journal_id FROM payment_allocations WHERE allocation_id = @id"
            + (_provider == "PostgreSQL" ? " FOR UPDATE" : "")))
        {
            Add(read, "@id", FormatGuid(allocationId));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The allocation was not found.");
            allocCompany = ReadString(reader, 0) ?? string.Empty;
            allocPayment = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            allocDocTable = ReadString(reader, 2) ?? string.Empty;
            allocDoc = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            allocDocNo = ReadString(reader, 4) ?? string.Empty;
            allocAmount = ReadDecimal(reader, 5);
            allocDocumentCompany = ReadString(reader, 6);
            allocAllocatedBy = ReadString(reader, 7);
            allocNote = ReadString(reader, 8);
            sourceKind = ReadString(reader, 9) ?? "PAYMENT";
            clearingJournalId = ReadString(reader, 10);
        }
        if (!string.Equals(allocCompany, companyId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("The allocation belongs to a different company.");
        if (!string.Equals(sourceKind, "ADVANCE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only advance allocations can be unallocated directly; reverse the payment voucher to release a normal payment allocation.");
        if (string.IsNullOrWhiteSpace(clearingJournalId) || !Guid.TryParse(clearingJournalId, out var clearingId))
            throw new InvalidOperationException($"Advance allocation for document '{allocDocNo}' has no clearing journal entry and cannot be reversed safely.");
        if (!PartyLedger.TableExists(connection, transaction, _provider, "payment_allocation_reversal_audit"))
            throw new InvalidOperationException("Payment-allocation reversal audit schema is missing. Apply database migrations before unallocating.");

        // Mirror the clearing entry first: a closed period or unbalanced
        // source fails the whole unallocation before anything is released.
        var reversalId = GlPosting.ReverseInTransaction(connection, transaction, _provider, clearingId, userId,
            $"Advance allocation {allocationId:D} reversed");

        var table = DocumentTable(allocDocTable);
        var documentCompany = allocDocumentCompany ?? allocCompany;
        decimal currentPaid;
        await using (var inspect = CreateCommand(connection, transaction,
            $"SELECT paid_amount FROM {table} WHERE CAST(company_id AS TEXT) IN (@documentCompany, @company) AND doc_id = @doc AND {NotDeleted}"))
        {
            Add(inspect, "@documentCompany", documentCompany);
            Add(inspect, "@company", allocCompany);
            Add(inspect, "@doc", allocDoc);
            var value = await inspect.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
                throw new InvalidOperationException($"Could not find {allocDocTable} document {allocDoc} while unallocating the advance.");
            currentPaid = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        if (currentPaid + Tolerance < allocAmount)
            throw new InvalidOperationException($"Cannot release {allocAmount} from {allocDocTable} document {allocDoc}: paid amount is {currentPaid}.");

        var isPg = _provider == "PostgreSQL";
        await using (var audit = CreateCommand(connection, transaction, """
            INSERT INTO payment_allocation_reversal_audit
                (audit_id, company_id, allocation_id, payment_ledger_id, doc_table, doc_id,
                 document_company_id, amount, allocated_by, note, released_by, reversal_journal_id)
            VALUES
                (@audit, @company, @allocation, @payment, @table, @doc,
                 @documentCompany, @amount, @allocatedBy, @note, @releasedBy, @reversal)
            """))
        {
            var auditId = Guid.NewGuid();
            Add(audit, "@audit", isPg ? (object)auditId : auditId.ToString("D"));
            Add(audit, "@company", allocCompany);
            Add(audit, "@allocation", isPg ? (object)allocationId : allocationId.ToString("D"));
            Add(audit, "@payment", allocPayment);
            Add(audit, "@table", allocDocTable);
            Add(audit, "@doc", allocDoc);
            Add(audit, "@documentCompany", documentCompany);
            Add(audit, "@amount", allocAmount);
            Add(audit, "@allocatedBy", (object?)allocAllocatedBy ?? DBNull.Value);
            Add(audit, "@note", (object?)allocNote ?? DBNull.Value);
            Add(audit, "@releasedBy", userId.Trim());
            Add(audit, "@reversal", isPg ? (object)reversalId : reversalId.ToString("D"));
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var release = CreateCommand(connection, transaction,
            $"""
            UPDATE {table}
            SET paid_amount = CASE
                WHEN CAST(paid_amount AS NUMERIC) <= CAST(@amount AS NUMERIC) + 0.005 THEN 0
                ELSE CAST(paid_amount AS NUMERIC) - CAST(@amount AS NUMERIC)
            END
            WHERE CAST(company_id AS TEXT) = @documentCompany AND doc_id = @doc AND {NotDeleted}
            """))
        {
            Add(release, "@amount", allocAmount);
            Add(release, "@documentCompany", documentCompany);
            Add(release, "@doc", allocDoc);
            if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException($"Could not release the advance allocation from {allocDocTable} document {allocDoc}.");
        }

        await using (var delete = CreateCommand(connection, transaction,
            "DELETE FROM payment_allocations WHERE allocation_id = @id AND company_id = @company"))
        {
            Add(delete, "@id", FormatGuid(allocationId));
            Add(delete, "@company", allocCompany);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The advance allocation changed while it was being reversed.");
        }
        return reversalId;
    }

    private async Task<Guid> ResolveCompanyGuidAsync(
        DbConnection connection, DbTransaction? transaction, string companyId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(companyId, out var suppliedGuid))
            return suppliedGuid;
        await using var command = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code");
        Add(command, "@code", companyId.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid))
            throw new InvalidOperationException($"Company '{companyId.Trim()}' could not be resolved to its journal company key.");
        return guid;
    }

    private async Task<string> ResolveDocumentCompanyKeyAsync(
        DbConnection connection, DbTransaction? transaction, string companyId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(companyId, out var suppliedGuid))
            return suppliedGuid.ToString("D");

        await using var command = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code");
        Add(command, "@code", companyId.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidOperationException($"Company '{companyId.Trim()}' could not be resolved to its operational document key.");
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException(
            $"Company '{companyId.Trim()}' has an invalid operational document key.");
    }

    private static string PartyKindToDocTable(string partyKind) => partyKind switch
    {
        "C" => "SALES",
        "S" => "PURCHASE",
        _ => throw new InvalidOperationException($"Unsupported party kind '{partyKind}'. Use 'C' or 'S'.")
    };

    private static string DocumentTable(string docTable) => docTable switch
    {
        "SALES" => "sales_documents",
        "PURCHASE" => "purchase_documents",
        _ => throw new InvalidOperationException($"Unsupported document table '{docTable}'.")
    };

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static void RequirePartyKind(string partyKind)
    {
        if (partyKind is not ("C" or "S"))
            throw new ArgumentException("Party kind must be 'C' (customer) or 'S' (supplier).", nameof(partyKind));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null)
            command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString();

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateOnly? ReadDate(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s when s.Length >= 10 && DateOnly.TryParse(s[..10], CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
