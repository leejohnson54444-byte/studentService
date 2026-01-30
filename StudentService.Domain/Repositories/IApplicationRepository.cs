using StudentService.Domain.DTOs;

namespace StudentService.Domain.Repositories
{
	public interface IApplicationRepository
	{
		Task<IEnumerable<ApplicationInfo>> GetByJobIdAsync(string jobId);
		Task<IEnumerable<ApplicationInfo>> GetByStudentIdAsync(string studentId);
		Task<ApplicationInfo> UpdateStatusAsync(string id, string? hiringStatus, bool isCompanyRatingPositive, bool isStudentRatingPositive);
	}
}
