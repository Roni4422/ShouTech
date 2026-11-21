using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral Quality persistence (migration 056), following the
/// BudgetRepository patterns: parameterized SQL only, company scoping
/// on every statement, soft deletes, single-transaction writes.
/// Company keying is the stock world's company CODE (see
/// IQualityRepository). Completion delegates its stock effects to
/// <see cref="QualityGate.CompleteInTransaction"/> inside the same
/// transaction, so status, movements, balances and the failure alert
/// commit or roll back together.
/// </summary>
public sealed class QualityRepository : IQualityRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public QualityRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string DeletedValue => IsPg ? "true" : "1";
    private string TrueValue => IsPg ? "true" : "1";
    private string FalseValue => IsPg ? "false" : "0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";

    // ------------------------------------------------------------------
    // Gate setting
    // ------------------------------------------------------------------
    public async Task<string> GetGateModeAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT gate_mode FROM quality_settings WHERE company_id = @c");
        Add(command, "@c", companyId.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var mode = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return QualityGateModes.All.Contains(mode ?? string.Empty) ? mode! : QualityGateModes.Default;
    }

    public async Task SetGateModeAsync(string companyId, string gateMode, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (!QualityGateModes.All.Contains(gateMode))
            throw new InvalidOperationException($"Unknown QC gate mode '{gateMode}'.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, IsPg
            ? """
              INSERT INTO quality_settings (company_id, gate_mode, updated_at, updated_by)
              VALUES (@c, @mode, now(), @user)
              ON CONFLICT (company_id) DO UPDATE SET gate_mode = EXCLUDED.gate_mode, updated_at = now(), updated_by = EXCLUDED.updated_by
              """
            : """
              INSERT INTO quality_settings (company_id, gate_mode, updated_at, updated_by)
              VALUES (@c, @mode, datetime('now'), @user)
              ON CONFLICT (company_id) DO UPDATE SET gate_mode = EXCLUDED.gate_mode, updated_at = datetime('now'), updated_by = EXCLUDED.updated_by
              """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@mode", gateMode);
        Add(command, "@user", RequireUser(userId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Parameters
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<QualityParameter>> GetParametersAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT param_id, code, name_en, name_ar, param_type, unit, group_name, is_active
            FROM quality_parameters WHERE company_id = @c AND {NotDeleted} ORDER BY code
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<QualityParameter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualityParameter
            {
                Id = ReadGuid(reader, 0),
                Code = ReadString(reader, 1) ?? string.Empty,
                NameEn = ReadString(reader, 2) ?? string.Empty,
                NameAr = ReadString(reader, 3) ?? string.Empty,
                ParamType = ReadString(reader, 4) ?? QualityParamTypes.Numeric,
                Unit = ReadString(reader, 5),
                GroupName = ReadString(reader, 6),
                IsActive = QualityGate.Truthy(reader.GetValue(7))
            });
        }
        return result;
    }

    public async Task SaveParameterAsync(string companyId, QualityParameter parameter, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(parameter);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM quality_parameters WHERE company_id = @c AND param_id = @id AND {NotDeleted}",
                companyId, parameter.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO quality_parameters
                        (param_id, company_id, code, name_en, name_ar, param_type, unit, group_name, is_active, created_by)
                    VALUES (@id, @c, @code, @en, @ar, @type, @unit, @grp, @active, @user)
                    """);
                BindParameter(insert, companyId, parameter, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE quality_parameters
                    SET code = @code, name_en = @en, name_ar = @ar, param_type = @type,
                        unit = @unit, group_name = @grp, is_active = @active,
                        updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND param_id = @id AND {NotDeleted}
                    """);
                BindParameter(update, companyId, parameter, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteParameterAsync(string companyId, Guid parameterId, string userId, CancellationToken cancellationToken = default)
        => await SoftDeleteAsync("quality_parameters", "param_id", companyId, parameterId, userId, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------
    // Templates
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<QualityTemplate>> GetTemplatesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var templates = new List<QualityTemplate>();
        await using (var command = CreateCommand(connection, null, $"""
            SELECT template_id, code, name_en, name_ar, inspection_type, is_active
            FROM quality_templates WHERE company_id = @c AND {NotDeleted} ORDER BY code
            """))
        {
            Add(command, "@c", companyId.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                templates.Add(new QualityTemplate
                {
                    Id = ReadGuid(reader, 0),
                    Code = ReadString(reader, 1) ?? string.Empty,
                    NameEn = ReadString(reader, 2) ?? string.Empty,
                    NameAr = ReadString(reader, 3) ?? string.Empty,
                    InspectionType = ReadString(reader, 4) ?? QualityInspectionTypes.Incoming,
                    IsActive = QualityGate.Truthy(reader.GetValue(5))
                });
            }
        }
        foreach (var template in templates)
        {
            await using var command = CreateCommand(connection, null, """
                SELECT l.template_line_id, l.param_id, p.code, p.name_en, p.param_type,
                       l.min_value, l.max_value, l.accepted_value, l.is_mandatory, l.sort
                FROM quality_template_lines l
                JOIN quality_parameters p ON p.param_id = l.param_id
                WHERE l.template_id = @t ORDER BY l.sort
                """);
            Add(command, "@t", FormatGuid(template.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                template.Lines.Add(new QualityTemplateLine
                {
                    Id = ReadGuid(reader, 0),
                    ParamId = ReadGuid(reader, 1),
                    ParamCode = ReadString(reader, 2) ?? string.Empty,
                    ParamName = ReadString(reader, 3),
                    ParamType = ReadString(reader, 4) ?? QualityParamTypes.Numeric,
                    MinValue = ReadDecimalOrNull(reader, 5),
                    MaxValue = ReadDecimalOrNull(reader, 6),
                    AcceptedValue = ReadString(reader, 7),
                    IsMandatory = QualityGate.Truthy(reader.GetValue(8)),
                    Sort = Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture)
                });
            }
        }
        return templates;
    }

    public async Task SaveTemplateAsync(string companyId, QualityTemplate template, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(template);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var line in template.Lines)
            {
                if (!await ExistsAsync(connection, transaction,
                        $"SELECT COUNT(*) FROM quality_parameters WHERE company_id = @c AND param_id = @id AND {NotDeleted}",
                        companyId, line.ParamId, cancellationToken).ConfigureAwait(false))
                    throw new AppError(ErrorCodes.QualityTemplateInvalid,
                        $"Template '{template.Code}' references a parameter that does not belong to this company.",
                        template.Code);
            }
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM quality_templates WHERE company_id = @c AND template_id = @id AND {NotDeleted}",
                companyId, template.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO quality_templates
                        (template_id, company_id, code, name_en, name_ar, inspection_type, is_active, created_by)
                    VALUES (@id, @c, @code, @en, @ar, @type, @active, @user)
                    """);
                BindTemplate(insert, companyId, template, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE quality_templates
                    SET code = @code, name_en = @en, name_ar = @ar, inspection_type = @type,
                        is_active = @active, updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND template_id = @id AND {NotDeleted}
                    """);
                BindTemplate(update, companyId, template, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Lines are replaced wholesale in the same transaction (the
            // editor's model, like budget lines).
            await using (var delete = CreateCommand(connection, transaction,
                "DELETE FROM quality_template_lines WHERE template_id = @t"))
            {
                Add(delete, "@t", FormatGuid(template.Id));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var line in template.Lines.OrderBy(l => l.Sort))
            {
                await using var insertLine = CreateCommand(connection, transaction, """
                    INSERT INTO quality_template_lines
                        (template_line_id, template_id, param_id, min_value, max_value, accepted_value, is_mandatory, sort)
                    VALUES (@id, @t, @param, @min, @max, @acc, @mand, @sort)
                    """);
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@t", FormatGuid(template.Id));
                Add(insertLine, "@param", FormatGuid(line.ParamId));
                Add(insertLine, "@min", (object?)line.MinValue ?? DBNull.Value);
                Add(insertLine, "@max", (object?)line.MaxValue ?? DBNull.Value);
                Add(insertLine, "@acc", (object?)line.AcceptedValue ?? DBNull.Value);
                Add(insertLine, "@mand", IsPg ? (object)line.IsMandatory : (line.IsMandatory ? 1 : 0));
                Add(insertLine, "@sort", line.Sort);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SoftDeleteTemplateAsync(string companyId, Guid templateId, string userId, CancellationToken cancellationToken = default)
        => await SoftDeleteAsync("quality_templates", "template_id", companyId, templateId, userId, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------
    // Material assignments
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<QualityMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT material_id, code, name_en, COALESCE(quality_check_required, 0),
                   COALESCE(qc_outgoing_required, 0), qc_template_in_id, qc_template_out_id
            FROM materials WHERE company_id = @c AND {NotDeleted} ORDER BY code
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<QualityMaterialOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualityMaterialOption(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ReadString(reader, 1) ?? string.Empty,
                ReadString(reader, 2) ?? string.Empty,
                QualityGate.Truthy(reader.GetValue(3)),
                QualityGate.Truthy(reader.GetValue(4)),
                QualityGate.ParseGuidOrNull(reader.GetValue(5)),
                QualityGate.ParseGuidOrNull(reader.GetValue(6))));
        }
        return result;
    }

    public async Task SetMaterialQualityAsync(string companyId, long materialId, bool qualityCheckRequired,
        bool outgoingRequired, Guid? templateInId, Guid? templateOutId, string userId,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE materials
            SET quality_check_required = @qc, qc_outgoing_required = @out,
                qc_template_in_id = @tin, qc_template_out_id = @tout,
                updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND material_id = @m AND {NotDeleted}
            """);
        // materials flag columns are INTEGER on both providers (013/015).
        Add(command, "@qc", qualityCheckRequired ? 1 : 0);
        Add(command, "@out", outgoingRequired ? 1 : 0);
        // The template columns are TEXT on both providers (they must
        // hold the SQLite Guid text and are informational on PG, where
        // templates are uuid — stored in their canonical text form).
        Add(command, "@tin", templateInId is Guid a ? a.ToString("D") : DBNull.Value);
        Add(command, "@tout", templateOutId is Guid b ? b.ToString("D") : DBNull.Value);
        Add(command, "@user", RequireUser(userId));
        Add(command, "@c", companyId.Trim());
        Add(command, "@m", materialId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Material was not found for this company.");
    }

    // ------------------------------------------------------------------
    // Inspections
    // ------------------------------------------------------------------
    private const string InspectionSelect = """
        SELECT i.inspection_id, i.inspection_type, i.template_id, i.ref_doc_kind, i.ref_doc_id,
               i.ref_doc_no, i.material_id, m.code, m.name_en, i.batch_id, b.batch_no,
               i.warehouse_id, i.quarantine_warehouse_id, i.party_id,
               i.total_qty, i.sample_qty, i.accepted_qty, i.rejected_qty,
               i.status, i.disposition, i.inspector, i.inspected_at, i.due_date, i.notes
        FROM quality_inspections i
        LEFT JOIN materials m ON m.material_id = i.material_id AND m.company_id = i.company_id
        LEFT JOIN stock_batches b ON b.batch_id = i.batch_id
        """ + " ";

    public async Task<IReadOnlyList<QualityInspection>> GetInspectionsAsync(string companyId, bool pendingOnly = false, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            InspectionSelect + $"""
            WHERE i.company_id = @c AND i.{NotDeleted}
            {(pendingOnly ? "AND i.status = 'Draft'" : "")}
            ORDER BY i.created_at DESC
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<QualityInspection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadInspection(reader));
        return result;
    }

    public async Task<IReadOnlyList<QualityInspection>> GetOverdueInspectionsAsync(string companyId, string asOfDate, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(asOfDate))
            throw new ArgumentException("An as-of date is required.", nameof(asOfDate));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            InspectionSelect + $"""
            WHERE i.company_id = @c AND i.{NotDeleted} AND i.status = 'Draft'
              AND i.due_date IS NOT NULL AND i.due_date < @asof
            ORDER BY i.due_date
            """);
        Add(command, "@c", companyId.Trim());
        Add(command, "@asof", asOfDate.Trim());
        var result = new List<QualityInspection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadInspection(reader));
        return result;
    }

    public async Task<QualityInspection?> GetInspectionAsync(string companyId, Guid inspectionId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        QualityInspection? inspection;
        await using (var command = CreateCommand(connection, null,
            InspectionSelect + $"WHERE i.company_id = @c AND i.inspection_id = @id AND i.{NotDeleted}"))
        {
            Add(command, "@c", companyId.Trim());
            Add(command, "@id", FormatGuid(inspectionId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            inspection = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadInspection(reader) : null;
        }
        if (inspection is null) return null;

        await using (var command = CreateCommand(connection, null, """
            SELECT l.line_id, l.param_id, p.code, p.name_en, p.param_type,
                   l.min_value, l.max_value, l.accepted_value, l.is_mandatory,
                   l.result, l.is_overridden, l.notes, l.sort
            FROM quality_inspection_lines l
            JOIN quality_parameters p ON p.param_id = l.param_id
            WHERE l.inspection_id = @id ORDER BY l.sort
            """))
        {
            Add(command, "@id", FormatGuid(inspectionId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inspection.Lines.Add(new QualityInspectionLine
                {
                    Id = ReadGuid(reader, 0),
                    ParamId = ReadGuid(reader, 1),
                    ParamCode = ReadString(reader, 2) ?? string.Empty,
                    ParamName = ReadString(reader, 3),
                    ParamType = ReadString(reader, 4) ?? QualityParamTypes.Numeric,
                    MinValue = ReadDecimalOrNull(reader, 5),
                    MaxValue = ReadDecimalOrNull(reader, 6),
                    AcceptedValue = ReadString(reader, 7),
                    IsMandatory = QualityGate.Truthy(reader.GetValue(8)),
                    Result = ReadString(reader, 9),
                    IsOverridden = QualityGate.Truthy(reader.GetValue(10)),
                    Notes = ReadString(reader, 11),
                    Sort = Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture)
                });
            }
        }
        foreach (var line in inspection.Lines)
        {
            await using var command = CreateCommand(connection, null, """
                SELECT reading_no, reading_num, reading_text
                FROM quality_inspection_readings WHERE line_id = @line ORDER BY reading_no
                """);
            Add(command, "@line", FormatGuid(line.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                line.Readings.Add(new QualityReading
                {
                    ReadingNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    ReadingNum = ReadDecimalOrNull(reader, 1),
                    ReadingText = ReadString(reader, 2)
                });
            }
        }
        return inspection;
    }

    public async Task<Guid> CreateInspectionAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(inspection);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = QualityGate.EnsureInspection(connection, transaction, _provider, companyId,
                inspection.InspectionType, inspection.RefDocKind, inspection.RefDocId, inspection.RefDocNo,
                inspection.MaterialId, inspection.BatchId, inspection.WarehouseId,
                inspection.QuarantineWarehouseId, inspection.PartyId, inspection.TotalQty,
                DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), RequireUser(userId));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveResultsAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(inspection);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var head = QualityGate.LoadHead(connection, transaction, _provider, companyId, inspection.Id)
                ?? throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                    "The inspection does not exist in the current company.", inspection.Id.ToString("D"));
            if (head.Status != QualityInspectionStatuses.Draft)
                throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                    $"Inspection for document '{head.RefDocNo}' is already completed with status '{head.Status}'.",
                    head.RefDocNo, head.Status);

            foreach (var line in inspection.Lines)
            {
                await using (var update = CreateCommand(connection, transaction, """
                    UPDATE quality_inspection_lines
                    SET result = @result, is_overridden = @over, notes = @notes
                    WHERE line_id = @id AND inspection_id = @insp
                    """))
                {
                    Add(update, "@result", (object?)line.Result ?? DBNull.Value);
                    Add(update, "@over", IsPg ? (object)line.IsOverridden : (line.IsOverridden ? 1 : 0));
                    Add(update, "@notes", (object?)line.Notes ?? DBNull.Value);
                    Add(update, "@id", FormatGuid(line.Id));
                    Add(update, "@insp", FormatGuid(inspection.Id));
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var delete = CreateCommand(connection, transaction,
                    "DELETE FROM quality_inspection_readings WHERE line_id = @line"))
                {
                    Add(delete, "@line", FormatGuid(line.Id));
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                foreach (var reading in line.Readings.OrderBy(r => r.ReadingNo))
                {
                    await using var insert = CreateCommand(connection, transaction, """
                        INSERT INTO quality_inspection_readings (reading_id, line_id, reading_no, reading_num, reading_text)
                        VALUES (@id, @line, @no, @num, @text)
                        """);
                    Add(insert, "@id", FormatGuid(Guid.NewGuid()));
                    Add(insert, "@line", FormatGuid(line.Id));
                    Add(insert, "@no", reading.ReadingNo);
                    Add(insert, "@num", (object?)reading.ReadingNum ?? DBNull.Value);
                    Add(insert, "@text", (object?)reading.ReadingText ?? DBNull.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await using (var touch = CreateCommand(connection, transaction, $"""
                UPDATE quality_inspections SET sample_qty = @sample, updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND inspection_id = @id
                """))
            {
                Add(touch, "@sample", inspection.SampleQty > 0 ? inspection.SampleQty : inspection.TotalQty);
                Add(touch, "@user", RequireUser(userId));
                Add(touch, "@c", companyId.Trim());
                Add(touch, "@id", FormatGuid(inspection.Id));
                await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<QualityCompletionResult> CompleteAsync(string companyId, Guid inspectionId,
        decimal acceptedQty, decimal rejectedQty, string? disposition,
        string inspector, string? notes, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var actor = RequireUser(inspector);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Auto-evaluate any line the inspector left unjudged, so a
            // completion never depends on every line being manually
            // resulted first; mandatory unjudged lines block.
            var full = await GetInspectionInTransactionAsync(connection, transaction, companyId, inspectionId, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                    "The inspection does not exist in the current company.", inspectionId.ToString("D"));
            foreach (var line in full.Lines)
            {
                var evaluated = QualityGate.EvaluateLine(new QualityLineEvaluationInput(
                    line.ParamType, line.MinValue, line.MaxValue, line.AcceptedValue,
                    line.Readings, line.Result));
                if (evaluated is null && line.IsMandatory)
                    throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                        $"Inspection line '{line.ParamCode}' for document '{full.RefDocNo}' has no readings and no result; a mandatory line must be judged before completion.",
                        full.RefDocNo, line.ParamCode);
                if (evaluated is not null && evaluated != line.Result)
                {
                    await using var update = CreateCommand(connection, transaction, """
                        UPDATE quality_inspection_lines SET result = @result WHERE line_id = @id
                        """);
                    Add(update, "@result", evaluated);
                    Add(update, "@id", FormatGuid(line.Id));
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var dateText = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var alertId = QualityGate.CompleteInTransaction(connection, transaction, _provider,
                companyId, inspectionId, acceptedQty, rejectedQty, disposition, actor, notes, dateText);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new QualityCompletionResult(inspectionId,
                QualityInspectionStatuses.FromQuantities(acceptedQty, rejectedQty),
                acceptedQty, rejectedQty, alertId);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<QualityInspection?> GetInspectionInTransactionAsync(
        DbConnection connection, DbTransaction transaction, string companyId, Guid inspectionId,
        CancellationToken cancellationToken)
    {
        // Lines + readings only (the head is loaded by the gate): enough
        // for the completion auto-evaluation.
        var inspection = new QualityInspection { Id = inspectionId };
        await using (var command = CreateCommand(connection, transaction, """
            SELECT l.line_id, l.param_id, p.code, p.name_en, p.param_type,
                   l.min_value, l.max_value, l.accepted_value, l.is_mandatory,
                   l.result, l.is_overridden, l.notes, l.sort, i.ref_doc_no
            FROM quality_inspection_lines l
            JOIN quality_parameters p ON p.param_id = l.param_id
            JOIN quality_inspections i ON i.inspection_id = l.inspection_id
            WHERE l.inspection_id = @id ORDER BY l.sort
            """))
        {
            Add(command, "@id", FormatGuid(inspectionId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inspection.RefDocNo = ReadString(reader, 13) ?? string.Empty;
                inspection.Lines.Add(new QualityInspectionLine
                {
                    Id = ReadGuid(reader, 0),
                    ParamId = ReadGuid(reader, 1),
                    ParamCode = ReadString(reader, 2) ?? string.Empty,
                    ParamName = ReadString(reader, 3),
                    ParamType = ReadString(reader, 4) ?? QualityParamTypes.Numeric,
                    MinValue = ReadDecimalOrNull(reader, 5),
                    MaxValue = ReadDecimalOrNull(reader, 6),
                    AcceptedValue = ReadString(reader, 7),
                    IsMandatory = QualityGate.Truthy(reader.GetValue(8)),
                    Result = ReadString(reader, 9),
                    IsOverridden = QualityGate.Truthy(reader.GetValue(10)),
                    Notes = ReadString(reader, 11),
                    Sort = Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture)
                });
            }
        }
        foreach (var line in inspection.Lines)
        {
            await using var command = CreateCommand(connection, transaction, """
                SELECT reading_no, reading_num, reading_text
                FROM quality_inspection_readings WHERE line_id = @line ORDER BY reading_no
                """);
            Add(command, "@line", FormatGuid(line.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                line.Readings.Add(new QualityReading
                {
                    ReadingNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    ReadingNum = ReadDecimalOrNull(reader, 1),
                    ReadingText = ReadString(reader, 2)
                });
            }
        }
        return inspection;
    }

    // ------------------------------------------------------------------
    // Alerts
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<QualityAlert>> GetAlertsAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT a.alert_id, a.alert_type, a.inspection_id, a.material_id, m.code,
                   a.batch_id, a.party_id, a.message, a.root_cause, a.corrective_action,
                   a.preventive_action, a.status, a.owner, a.due_date
            FROM quality_alerts a
            LEFT JOIN materials m ON m.material_id = a.material_id AND m.company_id = a.company_id
            WHERE a.company_id = @c AND a.{NotDeleted}
            {(openOnly ? "AND a.status <> 'Closed'" : "")}
            ORDER BY a.created_at DESC
            """);
        Add(command, "@c", companyId.Trim());
        var result = new List<QualityAlert>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualityAlert
            {
                Id = ReadGuid(reader, 0),
                AlertType = ReadString(reader, 1) ?? QualityAlertTypes.FailedInspection,
                InspectionId = ReadGuidOrNull(reader, 2),
                MaterialId = ReadLongOrNull(reader, 3),
                MaterialCode = ReadString(reader, 4),
                BatchId = ReadLongOrNull(reader, 5),
                PartyId = ReadLongOrNull(reader, 6),
                Message = ReadString(reader, 7),
                RootCause = ReadString(reader, 8),
                CorrectiveAction = ReadString(reader, 9),
                PreventiveAction = ReadString(reader, 10),
                Status = ReadString(reader, 11) ?? QualityAlertStatuses.Open,
                Owner = ReadString(reader, 12),
                DueDate = ReadString(reader, 13)
            });
        }
        return result;
    }

    public async Task SaveAlertAsync(string companyId, QualityAlert alert, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(alert);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE quality_alerts
            SET root_cause = @cause, corrective_action = @corr, preventive_action = @prev,
                status = @status, owner = @owner, updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND alert_id = @id AND {NotDeleted}
            """);
        Add(command, "@cause", (object?)alert.RootCause ?? DBNull.Value);
        Add(command, "@corr", (object?)alert.CorrectiveAction ?? DBNull.Value);
        Add(command, "@prev", (object?)alert.PreventiveAction ?? DBNull.Value);
        Add(command, "@status", alert.Status);
        Add(command, "@owner", (object?)alert.Owner ?? DBNull.Value);
        Add(command, "@user", RequireUser(userId));
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(alert.Id));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Quality alert was not found for this company.");
    }

    // ------------------------------------------------------------------
    // Reports
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<QualitySummaryRow>> GetSummaryAsync(string companyId, bool bySupplier, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = bySupplier
            ? $"""
              SELECT COALESCE(CAST(i.party_id AS TEXT), '-'), COALESCE(p.name_en, COALESCE(CAST(i.party_id AS TEXT), '-')),
                     COUNT(*), COALESCE(SUM(i.total_qty), 0), COALESCE(SUM(i.accepted_qty), 0), COALESCE(SUM(i.rejected_qty), 0)
              FROM quality_inspections i
              LEFT JOIN parties p ON CAST(p.id AS TEXT) = CAST(i.party_id AS TEXT)
              WHERE i.company_id = @c AND i.{NotDeleted} AND i.status <> 'Draft'
              GROUP BY i.party_id, p.name_en ORDER BY 1
              """
            : $"""
              SELECT COALESCE(m.code, CAST(i.material_id AS TEXT)), COALESCE(m.name_en, ''),
                     COUNT(*), COALESCE(SUM(i.total_qty), 0), COALESCE(SUM(i.accepted_qty), 0), COALESCE(SUM(i.rejected_qty), 0)
              FROM quality_inspections i
              LEFT JOIN materials m ON m.material_id = i.material_id AND m.company_id = i.company_id
              WHERE i.company_id = @c AND i.{NotDeleted} AND i.status <> 'Draft'
              GROUP BY i.material_id, m.code, m.name_en ORDER BY 1
              """;
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@c", companyId.Trim());
        var result = new List<QualitySummaryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualitySummaryRow(
                ReadString(reader, 0) ?? string.Empty,
                ReadString(reader, 1) ?? string.Empty,
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------
    private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction,
        string sql, string companyId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private async Task SoftDeleteAsync(string table, string idColumn, string companyId, Guid id, string userId, CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE {table} SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @c AND {idColumn} = @id AND {NotDeleted}");
        Add(command, "@user", RequireUser(userId));
        Add(command, "@c", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Record was not found for this company.");
    }

    private void BindParameter(DbCommand command, string companyId, QualityParameter p, string userId)
    {
        Add(command, "@id", FormatGuid(p.Id));
        Add(command, "@c", companyId.Trim());
        Add(command, "@code", p.Code.Trim());
        Add(command, "@en", p.NameEn.Trim());
        Add(command, "@ar", p.NameAr.Trim());
        Add(command, "@type", p.ParamType);
        Add(command, "@unit", (object?)p.Unit ?? DBNull.Value);
        Add(command, "@grp", (object?)p.GroupName ?? DBNull.Value);
        Add(command, "@active", IsPg ? (object)p.IsActive : (p.IsActive ? 1 : 0));
        Add(command, "@user", RequireUser(userId));
    }

    private void BindTemplate(DbCommand command, string companyId, QualityTemplate t, string userId)
    {
        Add(command, "@id", FormatGuid(t.Id));
        Add(command, "@c", companyId.Trim());
        Add(command, "@code", t.Code.Trim());
        Add(command, "@en", t.NameEn.Trim());
        Add(command, "@ar", t.NameAr.Trim());
        Add(command, "@type", t.InspectionType);
        Add(command, "@active", IsPg ? (object)t.IsActive : (t.IsActive ? 1 : 0));
        Add(command, "@user", RequireUser(userId));
    }

    private static QualityInspection ReadInspection(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        InspectionType = ReadString(reader, 1) ?? QualityInspectionTypes.Incoming,
        TemplateId = ReadGuidOrNull(reader, 2),
        RefDocKind = ReadString(reader, 3) ?? string.Empty,
        RefDocId = ReadString(reader, 4) ?? string.Empty,
        RefDocNo = ReadString(reader, 5) ?? string.Empty,
        MaterialId = Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
        MaterialCode = ReadString(reader, 7),
        MaterialName = ReadString(reader, 8),
        BatchId = ReadLongOrNull(reader, 9),
        BatchNo = ReadString(reader, 10),
        WarehouseId = Convert.ToInt64(reader.GetValue(11), CultureInfo.InvariantCulture),
        QuarantineWarehouseId = ReadLongOrNull(reader, 12),
        PartyId = ReadLongOrNull(reader, 13),
        TotalQty = Convert.ToDecimal(reader.GetValue(14), CultureInfo.InvariantCulture),
        SampleQty = Convert.ToDecimal(reader.GetValue(15), CultureInfo.InvariantCulture),
        AcceptedQty = Convert.ToDecimal(reader.GetValue(16), CultureInfo.InvariantCulture),
        RejectedQty = Convert.ToDecimal(reader.GetValue(17), CultureInfo.InvariantCulture),
        Status = ReadString(reader, 18) ?? QualityInspectionStatuses.Draft,
        Disposition = ReadString(reader, 19),
        Inspector = ReadString(reader, 20),
        InspectedAt = ReadString(reader, 21),
        DueDate = ReadString(reader, 22),
        Notes = ReadString(reader, 23)
    };

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string RequireUser(string userId)
        => string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("User is required.", nameof(userId))
            : userId.Trim();

    private DbConnection CreateConnection() => IsPg
        ? new NpgsqlConnection(_connectionString)
        : new SqliteConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
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

    private object FormatGuid(Guid value) => IsPg ? value : value.ToString("D");

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
        => Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static Guid? ReadGuidOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static long? ReadLongOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? ReadDecimalOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
