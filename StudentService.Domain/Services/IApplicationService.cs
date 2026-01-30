using StudentService.Domain.DTOs;

namespace StudentService.Domain.Services
{
	public interface IApplicationService
	{
		Task CreateAsync(ApplicationCreate applicationCreate);
	}
}
