using Microsoft.AspNetCore.Mvc;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Domain.Services;

namespace StudentService.API.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class ApplicationsController : ControllerBase
	{
		private readonly IApplicationRepository _applicationRepository;
		private readonly IStudentRecommendationService _studentRecommendationService;
		private readonly IApplicationService _applicationService;

		public ApplicationsController(
			IApplicationRepository applicationRepository,
			IStudentRecommendationService studentRecommendationService,
			IApplicationService applicationService)
		{
			_applicationRepository = applicationRepository;
			_studentRecommendationService = studentRecommendationService;
			_applicationService = applicationService;
		}

		[HttpGet("finished/{jobId}/job")]
		public async Task<IActionResult> GetByJobId(string jobId)
		{
			var result = await _applicationRepository.GetByJobIdAsync(jobId);
			return Ok(result);
		}

		[HttpGet("finished/{studentId}/student")]
		public async Task<IActionResult> GetByStudentId(string studentId)
		{
			var result = await _applicationRepository.GetByStudentIdAsync(studentId);
			return Ok(result);
		}

		[HttpGet("{jobId}/recommendations")]
		public async Task<IActionResult> GetRecommendedStudentsForJob(string jobId)
		{
			var result = await _studentRecommendationService.MLRecommendStudentsForJobAsync(jobId);
			return Ok(result);
		}

		[HttpGet("{jobId}/recommendations/basic")]
		public async Task<IActionResult> GetBasicRecommendedStudentsForJob(string jobId)
		{
			var result = await _studentRecommendationService.RecommendStudentsForJobAsync(jobId);
			return Ok(result);
		}

		[HttpPut("{id}/status")]
		public async Task<IActionResult> UpdateStatus(string id, [FromQuery] string status)
		{
			var result = await _applicationRepository.UpdateStatusAsync(id, status, false, false);
			return Ok(result);
		}

		[HttpPost]
		public async Task<IActionResult> Create([FromBody] ApplicationCreate applicationCreate)
		{
			await _applicationService.CreateAsync(applicationCreate);
			return Ok();
		}
	}
}
