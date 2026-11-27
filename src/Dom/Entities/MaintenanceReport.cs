namespace ShouTech.Domain.Entities;

/// <summary>Immutable maintenance report snapshot stored in the active company database.</summary>
public sealed class MaintenanceReportRecord
{
    public long ReportNo { get; set; }
    public string RunAt { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CheckCount { get; set; }
    public int PassedCount { get; set; }
    public int WarningCount { get; set; }
    public int FailedCount { get; set; }
    public string ReportJson { get; set; } = "{}";
}
