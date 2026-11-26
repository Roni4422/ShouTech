using System;
using System.Threading.Tasks;

namespace ShouTech.Core.Security
{
    public interface ISecurityEngine
    {
        Task ValidatePermissionAsync(Guid userId, string permissionKey);
    }

    /// <summary>
    /// Explicitly denies permission checks. This placeholder must never grant access.
    /// Replace it with the application's configured authorization provider before
    /// enabling protected operations.
    /// </summary>
    [Obsolete("DummySecurityEngine denies all permissions. Register a real authorization provider.")]
    public sealed class DummySecurityEngine : ISecurityEngine
    {
        public Task ValidatePermissionAsync(Guid userId, string permissionKey)
        {
            return Task.FromException(
                new UnauthorizedAccessException("No authorization provider is configured."));
        }
    }
}
