using Microsoft.AspNetCore.Mvc;
using StudentService.Domain.Services;

namespace StudentService.API.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class ModelsController : ControllerBase
	{
		private readonly IModelManagementService _modelManagementService;
		private readonly ILogger<ModelsController> _logger;

		public ModelsController(
			IModelManagementService modelManagementService,
			ILogger<ModelsController> logger)
		{
			_modelManagementService = modelManagementService;
			_logger = logger;
		}

		[HttpPost("train")]
		public async Task<IActionResult> TrainAllModels()
		{
			_logger.LogInformation("Training all models requested");
			var result = await _modelManagementService.TrainAllModelsAsync();
			return Ok(result);
		}

		[HttpPost("train/{modelType}")]
		public async Task<IActionResult> TrainModel(string modelType)
		{
			_logger.LogInformation("Training model requested: {ModelType}", modelType);
			var result = await _modelManagementService.TrainModelAsync(modelType);
			
			if (!result.Success)
				return BadRequest(result);
			
			return Ok(result);
		}

		[HttpGet("{modelType}/versions")]
		public async Task<IActionResult> GetModelVersions(string modelType)
		{
			var versions = await _modelManagementService.GetModelVersionsAsync(modelType);
			return Ok(versions);
		}

		[HttpGet("{modelType}/production")]
		public async Task<IActionResult> GetProductionModel(string modelType)
		{
			var model = await _modelManagementService.GetProductionModelAsync(modelType);
			
			if (model == null)
				return NotFound($"No production model found for {modelType}");
			
			return Ok(model);
		}

		[HttpPost("{modelType}/promote/{version}")]
		public async Task<IActionResult> PromoteToProduction(string modelType, string version)
		{
			_logger.LogInformation("Promoting model {ModelType} version {Version} to production", modelType, version);
			var success = await _modelManagementService.PromoteToProductionAsync(modelType, version);
			
			if (!success)
				return BadRequest($"Failed to promote {modelType} version {version} to production");
			
			return Ok(new { message = $"Model {modelType} version {version} promoted to production" });
		}

		[HttpPost("{modelType}/rollback")]
		public async Task<IActionResult> RollbackProductionModel(string modelType)
		{
			_logger.LogInformation("Rolling back production model: {ModelType}", modelType);
			var success = await _modelManagementService.RollbackProductionModelAsync(modelType);
			
			if (!success)
				return BadRequest($"Failed to rollback {modelType} production model. No previous version available or rollback failed.");
			
			return Ok(new { message = $"Model {modelType} rolled back to previous version" });
		}

		[HttpGet("status")]
		public IActionResult GetTrainingStatus()
		{
			var status = _modelManagementService.GetTrainingStatus();
			return Ok(status);
		}

		[HttpPost("cache/invalidate")]
		public IActionResult InvalidateCache([FromQuery] string? modelType = null)
		{
			_modelManagementService.InvalidateModelCache(modelType);
			var message = string.IsNullOrEmpty(modelType) 
				? "All model caches invalidated" 
				: $"Cache invalidated for {modelType}";
			return Ok(new { message });
		}

		[HttpGet("types")]
		public IActionResult GetModelTypes()
		{
			return Ok(new[] { "JobRecommendation", "StudentRecommendation", "JobPayPrediction" });
		}
	}
}
