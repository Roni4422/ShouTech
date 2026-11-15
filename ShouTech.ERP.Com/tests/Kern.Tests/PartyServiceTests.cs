using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

public sealed class PartyServiceTests
{
    [Fact]
    public async Task SaveAndReload_PreservesPartyFieldsAndCompanyScope()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-party-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString();
            var companyId = Guid.NewGuid();
            var otherCompanyId = Guid.NewGuid();

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE companies (company_id TEXT PRIMARY KEY);
                    INSERT INTO companies(company_id) VALUES (@company), (@otherCompany);
                    CREATE TABLE parties (
                        party_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        company_id TEXT NOT NULL REFERENCES companies(company_id),
                        party_code TEXT NOT NULL,
                        party_kind TEXT NOT NULL CHECK (party_kind IN ('C', 'S', 'B')),
                        name_en TEXT NOT NULL,
                        name_ar TEXT,
                        group_code TEXT,
                        tax_number TEXT,
                        commercial_registration TEXT,
                        credit_limit NUMERIC NOT NULL DEFAULT 0 CHECK (credit_limit >= 0),
                        credit_days INTEGER NOT NULL DEFAULT 0 CHECK (credit_days >= 0),
                        price_tier TEXT,
                        gl_account_code TEXT,
                        phone TEXT,
                        mobile TEXT,
                        email TEXT,
                        address TEXT,
                        is_active INTEGER NOT NULL DEFAULT 1,
                        is_deleted INTEGER NOT NULL DEFAULT 0,
                        created_by TEXT,
                        created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        updated_by TEXT,
                        updated_at TEXT,
                        UNIQUE(company_id, party_kind, party_code)
                    );
                    """;
                command.Parameters.AddWithValue("@company", companyId.ToString("D"));
                command.Parameters.AddWithValue("@otherCompany", otherCompanyId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            var service = new PartyService(new PartyRepository("SQLite", connectionString));
            await service.SaveAsync(new PartyDraft(
                companyId, "CUS-001", "C", "Northwind Trading", "نورث ويند",
                "WHOLESALE", "KW123456", "CR-7788", 2500.750m, 45,
                "Wholesale", "121101", "+96512345678", "+96598765432",
                "accounts@example.test", "Kuwait City", true, "test-user"));

            var parties = await service.GetAsync(companyId, "c");
            var party = Assert.Single(parties);
            Assert.Equal("CUS-001", party.Code);
            Assert.Equal("Northwind Trading", party.NameEnglish);
            Assert.Equal("نورث ويند", party.NameArabic);
            Assert.Equal(2500.750m, party.CreditLimit);
            Assert.Equal(45, party.CreditDays);
            Assert.Equal("KW123456", party.TaxNumber);
            Assert.Equal("accounts@example.test", party.Email);
            Assert.Empty(await service.GetAsync(otherCompanyId, "C"));
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Save_RejectsInvalidKindAndNegativeCredit()
    {
        var service = new PartyService(new RecordingPartyRepository());
        var companyId = Guid.NewGuid();
        var invalidKind = new PartyDraft(companyId, "P-1", "X", "Party", null, null, null, null,
            0m, 0, null, null, null, null, null, null, true, null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(invalidKind));

        var negativeCredit = invalidKind with { Kind = "C", CreditLimit = -1m };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveAsync(negativeCredit));
    }

    private sealed class RecordingPartyRepository : IPartyRepository
    {
        public Task<IReadOnlyList<Party>> GetAsync(Guid companyId, string kind, bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Party>>(Array.Empty<Party>());

        public Task SaveAsync(PartyDraft party, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
