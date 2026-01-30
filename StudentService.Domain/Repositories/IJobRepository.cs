using StudentService.Domain.DTOs;

namespace StudentService.Domain.Repositories
{
	public interface IJobRepository
	{
		Task<IEnumerable<JobBasicInfo>> GetAllAsync();
		Task<IEnumerable<JobBasicInfo>> GetByCompanyIdAsync(string companyId);
		Task<JobBasicInfo> GetBasicByIdAsync(string jobId);
		Task<JobDetailInfo> GetDetailedByIdAsync(string jobId);
	}
}
