
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShouTech.Dom.Accounting
{
    // Immutable record ensures no accidental mutation after creation
    public record JournalEntryLine(
        string AccountId,
        decimal Debit,
        decimal Credit,
        string Description
    );

    public class ImmutableJournalEntry
    {
        public Guid Id { get; private set; } = Guid.NewGuid();
        public string CompanyId { get; private set; }
        public DateTime PostingDate { get; private set; }
        public string DocumentReference { get; private set; }
        public bool IsReversal { get; private set; }
        public Guid? ReversesEntryId { get; private set; }
        public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
        
        private readonly List<JournalEntryLine> _lines = new();
        public IReadOnlyList<JournalEntryLine> Lines => _lines.AsReadOnly();

        private ImmutableJournalEntry() { } // For ORM

        public ImmutableJournalEntry(string companyId, string documentReference, DateTime postingDate)
        {
            CompanyId = companyId ?? throw new ArgumentNullException(nameof(companyId));
            DocumentReference = documentReference ?? throw new ArgumentNullException(nameof(documentReference));
            PostingDate = postingDate;
        }

        public void AddLine(string accountId, decimal debit, decimal credit, string description)
        {
            if (IsPosted) throw new InvalidOperationException("Cannot modify a posted journal entry.");
            if (debit < 0 || credit < 0) throw new ArgumentException("Debit and credit must be non-negative.");
            
            _lines.Add(new JournalEntryLine(accountId, debit, credit, description));
        }

        public bool IsBalanced => Math.Abs(TotalDebits - TotalCredits) < 0.0001m;
        public decimal TotalDebits => _lines.Sum(l => l.Debit);
        public decimal TotalCredits => _lines.Sum(l => l.Credit);
        public bool IsPosted { get; private set; }

        public void Post()
        {
            if (IsPosted) return;
            if (!IsBalanced) throw new InvalidOperationException($"Journal entry {Id} is unbalanced. Debits: {TotalDebits}, Credits: {TotalCredits}");
            if (_lines.Count == 0) throw new InvalidOperationException("Journal entry must have at least one line.");
            
            IsPosted = true;
        }

        // Creates a new, distinct reversing entry rather than mutating this one
        public ImmutableJournalEntry CreateReversal(string reversalReference, DateTime reversalDate)
        {
            if (!IsPosted) throw new InvalidOperationException("Cannot reverse an unposted entry.");
            
            var reversal = new ImmutableJournalEntry(this.CompanyId, reversalReference, reversalDate)
            {
                IsReversal = true,
                ReversesEntryId = this.Id
            };

            foreach (var line in this._lines)
            {
                // Swap debit and credit to reverse the accounting effect
                reversal.AddLine(line.AccountId, line.Credit, line.Debit, $"Reversal of {this.DocumentReference}: {line.Description}");
            }

            return reversal;
        }
    }
}
