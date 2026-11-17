namespace ShouTech.Domain.Entities;

/// <summary>Project statuses (stored as text, validated in the service layer).</summary>
public static class ProjectStatuses
{
    public const string Planned = "Planned";
    public const string Active = "Active";
    public const string OnHold = "OnHold";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = new[] { Planned, Active, OnHold, Completed, Cancelled };
}

/// <summary>Task statuses inside a project.</summary>
public static class ProjectTaskStatuses
{
    public const string ToDo = "ToDo";
    public const string InProgress = "InProgress";
    public const string Done = "Done";

    public static readonly IReadOnlyList<string> All = new[] { ToDo, InProgress, Done };
}

/// <summary>A customer/internal project with a budget and a date window.</summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string Status { get; set; } = ProjectStatuses.Planned;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public decimal Budget { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One task belonging to a project.</summary>
public sealed class ProjectTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = ProjectTaskStatuses.ToDo;
    public DateOnly? DueDate { get; set; }
    public string? AssignedTo { get; set; }
    public string? Notes { get; set; }
}
