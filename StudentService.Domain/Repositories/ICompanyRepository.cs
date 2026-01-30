using StudentService.Domain.DTOs;

namespace StudentService.Domain.Repositories
{
	public interface ICompanyRepository
	{
		Task<IEnumerable<CompanyInfo>> GetAllAsync();
		Task<CompanyInfo> GetByIdAsync(string id);
		Task<IEnumerable<CompanyInfo>> GetByNameAsync(string name);
	}
}
