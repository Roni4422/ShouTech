using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Cheque persistence (SQLite + PostgreSQL): parameterized SQL
/// only, company scoping on every statement, single-transaction writes.
/// </summary>
public sealed class ChequeRepository : IChequeRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public ChequeRepository(string provider, string connectionString)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    internal string Provider => _provider;
    internal string ConnectionString => _connectionString;
}
