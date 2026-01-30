using StudentService.Domain.DTOs;

namespace StudentService.Domain.Repositories
{
	public interface IStudentRepository
	{
		Task<StudentBasicInfo> GetByApplicationIdAsync(string applicationId);
		Task<StudentDetailInfo> GetDetailedInfoByIdAsync(string studentId);
		Task<StudentBasicInfo> GetBasicInfoByIdAsync(string studentId);
	}
}
