using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> GetAllAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveAsync(string companyId, Project project, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(string companyId, Guid projectId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectTask>> GetTasksAsync(string companyId, Guid projectId, CancellationToken cancellationToken = default);
    Task SaveTaskAsync(string companyId, ProjectTask task, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteTaskAsync(string companyId, Guid taskId, string userId, CancellationToken cancellationToken = default);
}
