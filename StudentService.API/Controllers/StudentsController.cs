using Microsoft.AspNetCore.Mvc;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Domain.Services;

namespace StudentService.API.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class StudentsController : ControllerBase
	{
		private readonly IStudentRepository _studentRepository;
		private readonly IStudentApplicationService _studentService;

		public StudentsController(IStudentRepository studentRepository, IStudentApplicationService studentService)
		{
			_studentRepository = studentRepository;
			_studentService = studentService;
		}

		[HttpGet("{applicationId}/application")]
		public async Task<IActionResult> GetByApplicationId(string applicationId)
		{
			var result = await _studentRepository.GetByApplicationIdAsync(applicationId);
			return Ok(result);
		}

		[HttpGet("{id}/detailed")]
		public async Task<IActionResult> GetDetailedInfoById(string id)
		{
			var result = await _studentRepository.GetDetailedInfoByIdAsync(id);
			return Ok(result);
		}

		[HttpGet("{id}/basic")]
		public async Task<IActionResult> GetBasicInfoById(string id)
		{
			var result = await _studentRepository.GetBasicInfoByIdAsync(id);
			return Ok(result);
		}

		[HttpPut("rating")]
		public async Task<IActionResult> UpdateRatingById([FromBody] StudentUpdate studentUpdate)
		{
			await _studentService.UpdateRatingByIdAsync(studentUpdate);
			return Ok();
		}
	}
}
