using Microsoft.AspNetCore.Mvc;
using StudentService.Domain.Repositories;
using StudentService.Domain.Services;
using StudentService.Domain.Services.Models;
using StudentService.Domain.ValueObjects;

namespace StudentService.API.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class JobsController : ControllerBase
	{
		private readonly IJobRepository _jobRepository;
		private readonly IJobRecommendationService _jobRecommendationService;
		private readonly IJobService _jobService;
		private readonly IJobPayPredictionService _payPredictionService;
		private readonly IJobSearchService _jobSearchService;

		public JobsController(
			IJobRepository jobRepository,
			IJobRecommendationService jobRecommendationService,
			IJobService jobService,
			IJobPayPredictionService payPredictionService,
			IJobSearchService jobSearchService)
		{
			_jobRepository = jobRepository;
			_jobRecommendationService = jobRecommendationService;
			_jobService = jobService;
			_payPredictionService = payPredictionService;
			_jobSearchService = jobSearchService;
		}

		[HttpGet]
		public async Task<IActionResult> GetAll()
		{
			var result = await _jobRepository.GetAllAsync();
			return Ok(result);
		}

		[HttpGet("{companyId}/company")]
		public async Task<IActionResult> GetByCompanyId(string companyId)
		{
			var result = await _jobRepository.GetByCompanyIdAsync(companyId);
			return Ok(result);
		}

		[HttpGet("{id}/detailed")]
		public async Task<IActionResult> GetDetailedById(string id)
		{
			var result = await _jobRepository.GetDetailedByIdAsync(id);
			return Ok(result);
		}

		[HttpGet("{id}/basic")]
		public async Task<IActionResult> GetBasicById(string id)
		{
			var result = await _jobRepository.GetBasicByIdAsync(id);
			return Ok(result);
		}

		[HttpGet("{studentId}/recommendations")]
		public async Task<IActionResult> GetRecommendedJobsForStudent(string studentId)
		{
			var result = await _jobRecommendationService.MLRecommendJobsForStudentAsync(studentId);
			return Ok(result);
		}

		[HttpGet("{studentId}/recommendations/basic")]
		public async Task<IActionResult> GetBasicRecommendedJobsForStudent(string studentId)
		{
			var result = await _jobRecommendationService.RecommendJobsForStudentAsync(studentId);
			return Ok(result);
		}

		[HttpPut("{id}/rating/company")]
		public async Task<IActionResult> UpdateRatingById(string id, string studentId, [FromQuery] bool isPositiveRating)
		{
			await _jobService.UpdateRatingByIdAsync(id, studentId, isPositiveRating);
			return Ok();
		}

		[HttpPost("predict/{algorithm}")]
		public async Task<IActionResult> PredictPay(string algorithm, [FromBody] PredictPayRequest request)
		{
			var input = new JobPayPredictionInput
			{
				Type = request.Type,
				PlaceOfWork = request.PlaceOfWork,
				RequiredTraits = request.RequiredTraits
			};
			var prediction = await _payPredictionService.PredictAsync(algorithm, input);
			return Ok(prediction);
		}

		[HttpGet("ml/metrics")]
		public async Task<IActionResult> GetAllAlgorithmMetrics()
		{
			var result = await _payPredictionService.EvaluateAllAlgorithmsAsync();
			return Ok(result);
		}

		[HttpGet("search")]
		public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
		{
			var results = await _jobSearchService.SearchAsync(q, page, pageSize);
			return Ok(results);
		}
	}
}
