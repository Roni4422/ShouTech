using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
namespace ShouTech.Infrastructure;

/// <summary>Metadata-only attachment repository; every query is scoped by company and active rows.</summary>
public sealed class PurchaseDocumentAttachmentRepository:IPurchaseDocumentAttachmentRepository
{
    private readonly string _provider,_connection;private bool Pg=>_provider=="PostgreSQL";
    public PurchaseDocumentAttachmentRepository(string provider,string connection){if(string.IsNullOrWhiteSpace(connection))throw new ArgumentException("Connection string is required.",nameof(connection));_provider=GlPosting.NormalizeProvider(provider);_connection=connection;}
    public async Task AddAsync(PurchaseDocumentAttachment a,CancellationToken ct=default)
    {await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var q=Cmd(c,"INSERT INTO purchase_document_attachments(attachment_id,company_id,document_id,storage_key,original_name,media_type,byte_length,sha256,uploaded_by,uploaded_at,is_deleted) VALUES(@id,@c,@d,@k,@n,@m,@l,@h,@u,@at,@false)");Bind(q,a);await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
    public async Task<IReadOnlyList<PurchaseDocumentAttachment>> ListAsync(Guid company,long doc,CancellationToken ct=default)
    {await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);var rows=new List<PurchaseDocumentAttachment>();await using var q=Cmd(c,"SELECT attachment_id,company_id,document_id,storage_key,original_name,media_type,byte_length,sha256,uploaded_by,uploaded_at FROM purchase_document_attachments WHERE company_id=@c AND document_id=@d AND is_deleted="+(Pg?"false":"0")+" ORDER BY uploaded_at DESC");Add(q,"@c",Co(company));Add(q,"@d",doc);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);while(await r.ReadAsync(ct).ConfigureAwait(false))rows.Add(Read(r));return rows;}
    public async Task<PurchaseDocumentAttachment?> FindAsync(Guid company,Guid id,CancellationToken ct=default)
    {await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var q=Cmd(c,"SELECT attachment_id,company_id,document_id,storage_key,original_name,media_type,byte_length,sha256,uploaded_by,uploaded_at FROM purchase_document_attachments WHERE company_id=@c AND attachment_id=@id AND is_deleted="+(Pg?"false":"0"));Add(q,"@c",Co(company));Add(q,"@id",Id(id));await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);return await r.ReadAsync(ct).ConfigureAwait(false)?Read(r):null;}
    public async Task SoftDeleteAsync(Guid company,Guid id,string actor,CancellationToken ct=default)
    {await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var q=Cmd(c,"UPDATE purchase_document_attachments SET is_deleted="+(Pg?"true":"1")+",deleted_by=@u,deleted_at="+(Pg?"now()":"datetime('now') ")+" WHERE company_id=@c AND attachment_id=@id AND is_deleted="+(Pg?"false":"0"));Add(q,"@u",actor);Add(q,"@c",Co(company));Add(q,"@id",Id(id));if(await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new InvalidOperationException("Attachment is missing or already deleted.");}
    private void Bind(DbCommand q,PurchaseDocumentAttachment a){Add(q,"@id",Id(a.Id));Add(q,"@c",Co(a.CompanyId));Add(q,"@d",a.DocumentId);Add(q,"@k",a.StorageKey);Add(q,"@n",a.OriginalName);Add(q,"@m",a.MediaType);Add(q,"@l",a.ByteLength);Add(q,"@h",a.Sha256);Add(q,"@u",a.UploadedBy);Add(q,"@at",a.UploadedAt);Add(q,"@false",Pg?false:0);}
    private PurchaseDocumentAttachment Read(DbDataReader r)=>new(Guid.Parse(Convert.ToString(r.GetValue(0),CultureInfo.InvariantCulture)!),Guid.Parse(Convert.ToString(r.GetValue(1),CultureInfo.InvariantCulture)!),Convert.ToInt64(r.GetValue(2),CultureInfo.InvariantCulture),Convert.ToString(r.GetValue(3),CultureInfo.InvariantCulture)!,Convert.ToString(r.GetValue(4),CultureInfo.InvariantCulture)!,Convert.ToString(r.GetValue(5),CultureInfo.InvariantCulture)!,Convert.ToInt64(r.GetValue(6),CultureInfo.InvariantCulture),Convert.ToString(r.GetValue(7),CultureInfo.InvariantCulture)!,Convert.ToString(r.GetValue(8),CultureInfo.InvariantCulture)!,Convert.ToDateTime(r.GetValue(9),CultureInfo.InvariantCulture));
    private object Co(Guid id)=>Pg?id:id.ToString("D");private object Id(Guid id)=>Pg?id:id.ToString("D");
    private DbConnection Open()=>Pg?new NpgsqlConnection(_connection):new SqliteConnection(_connection);
    private static DbCommand Cmd(DbConnection c,string sql){var q=c.CreateCommand();q.CommandText=sql;return q;}
    private static void Add(DbCommand q,string n,object v){var p=q.CreateParameter();p.ParameterName=n;p.Value=v;q.Parameters.Add(p);}
}
