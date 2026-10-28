// cod/cor/invuow.cs
using System;
using System.Data;

namespace ShouTech.Core.Inv
{
    public class UnitOfWork : IDisposable
    {
        private readonly IDbConnection _conn;
        private IDbTransaction? _tx;

        public InventoryRepository Inv { get; }

        public UnitOfWork(IDbConnection conn)
        {
            _conn = conn;
            Inv = new InventoryRepository(conn);
        }

        public void Begin() => _tx = _conn.BeginTransaction();
        public void Commit() => _tx?.Commit();
        public void Rollback() => _tx?.Rollback();
        public void Dispose() { _tx?.Dispose(); _conn?.Dispose(); }
    }
}