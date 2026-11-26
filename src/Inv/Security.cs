// cod/cor/Security.cs
using System;
using System.Threading.Tasks;

namespace ShouTech.Core.Security
{
    public interface ISecurityEngine
    {
        Task ValidatePermissionAsync(Guid userId, string permissionKey);
    }

    public class DummySecurityEngine : ISecurityEngine
    {
        public Task ValidatePermissionAsync(Guid userId, string permissionKey)
        {
            return Task.CompletedTask;
        }
    }
}