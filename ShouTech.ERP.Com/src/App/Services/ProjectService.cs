using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Projects application service. All invariants are validated here before the
/// repository is touched (the repository re-checks uniqueness/company scope
/// inside its transaction as a second line of defence).
/// </summary>
public sealed class ProjectService
{
    private readonly IProjectRepository _repository;

    public ProjectService(IProjectRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IReadOnlyList<Project>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveAsync(string companyId, Project project, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.Code))
            throw new InvalidOperationException("Project code is required.");
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new InvalidOperationException("Project name is required.");
        if (!ProjectStatuses.All.Contains(project.Status))
            throw new InvalidOperationException($"Unknown project status '{project.Status}'.");
        if (project.Budget < 0)
            throw new InvalidOperationException("Project budget cannot be negative.");
        if (project.StartDate.HasValue && project.EndDate.HasValue && project.EndDate.Value < project.StartDate.Value)
            throw new InvalidOperationException("Project end date cannot be before its start date.");
        if (project.CustomerId.HasValue && project.CustomerId.Value <= 0)
            throw new InvalidOperationException("Customer ID must be positive when supplied.");

        project.Code = project.Code.Trim();
        project.Name = project.Name.Trim();
        await _repository.SaveAsync(companyId, project, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return project.Id;
    }

    public Task DeleteAsync(string companyId, Guid projectId, string userId, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Project is required.", nameof(projectId));
        return _repository.SoftDeleteAsync(RequireCompany(companyId), projectId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<ProjectTask>> GetTasksAsync(string companyId, Guid projectId, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Project is required.", nameof(projectId));
        return _repository.GetTasksAsync(RequireCompany(companyId), projectId, cancellationToken);
    }

    public async Task<Guid> SaveTaskAsync(string companyId, ProjectTask task, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(task);
        if (task.ProjectId == Guid.Empty)
            throw new InvalidOperationException("A task must belong to a project.");
        if (string.IsNullOrWhiteSpace(task.Name))
            throw new InvalidOperationException("Task name is required.");
        if (!ProjectTaskStatuses.All.Contains(task.Status))
            throw new InvalidOperationException($"Unknown task status '{task.Status}'.");

        task.Name = task.Name.Trim();
        await _repository.SaveTaskAsync(companyId, task, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return task.Id;
    }

    public Task DeleteTaskAsync(string companyId, Guid taskId, string userId, CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task is required.", nameof(taskId));
        return _repository.SoftDeleteTaskAsync(RequireCompany(companyId), taskId, RequireUser(userId), cancellationToken);
    }

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }
}
