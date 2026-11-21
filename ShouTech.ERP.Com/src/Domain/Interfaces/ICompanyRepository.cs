using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces
{
    public interface ICompanyRepository
    {
        Task<List<Comp>> GetAllAsync();
        Task<Comp?> GetByCodeAsync(string code);
        Task<bool> HasCompaniesAsync();
        Task AddAsync(Comp Comp);
    }
}