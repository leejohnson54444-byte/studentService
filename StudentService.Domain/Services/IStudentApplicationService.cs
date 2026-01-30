using StudentService.Domain.DTOs;

namespace StudentService.Domain.Services
{
	public interface IStudentApplicationService
	{
		public Task UpdateRatingByIdAsync(StudentUpdate studentUpdateDto);
	}
}
