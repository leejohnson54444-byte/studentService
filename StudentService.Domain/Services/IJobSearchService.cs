using StudentService.Domain.DTOs;

namespace StudentService.Domain.Services
{
	public interface IJobSearchService
	{
		Task<IEnumerable<JobBasicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20);
		Task ReindexAllAsync();
	}
}
