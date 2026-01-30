using Microsoft.Extensions.Logging;
using StudentService.Domain.Services;
using StudentService.Infrastructure.MLflow;

namespace StudentService.Infrastructure.Services
{
	public class ModelManagementService : IModelManagementService
	{
		private readonly ModelTrainingService _trainingService;
		private readonly AutomaticModelTrainingService _autoTrainingService;
		private readonly MLflowClient _mlflowClient;
		private readonly ILogger<ModelManagementService> _logger;

		public ModelManagementService(
			ModelTrainingService trainingService,
			AutomaticModelTrainingService autoTrainingService,
			MLflowClient mlflowClient,
			ILogger<ModelManagementService> logger)
		{
			_trainingService = trainingService;
			_autoTrainingService = autoTrainingService;
			_mlflowClient = mlflowClient;
			_logger = logger;
		}

		public async Task<ModelTrainingResultDto> TrainAllModelsAsync()
		{
			_logger.LogInformation("Starting training for all models");

			var results = await _autoTrainingService.TrainAllModelsAsync();

			return new ModelTrainingResultDto
			{
				Success = results.All(r => r.Success),
				AllResults = results.Select(r => new ModelTrainingResultDto
				{
					Success = r.Success,
					RunId = r.RunId,
					Metrics = r.Metrics?.ToDictionary(),
					PromotedToProduction = r.PromotedToProduction,
					Message = r.Message,
					PreviousProductionRunId = r.PreviousProductionRunId
				}).ToList(),
				Message = $"Trained {results.Count(r => r.Success)}/{results.Count} models successfully, {results.Count(r => r.PromotedToProduction)} promoted to production"
			};
		}

		public async Task<ModelTrainingResultDto> TrainModelAsync(string modelType)
		{
			if (!Enum.TryParse<ModelType>(modelType, true, out var type))
			{
				return new ModelTrainingResultDto
				{
					Success = false,
					Message = $"Unknown model type: {modelType}. Valid types: {string.Join(", ", Enum.GetNames<ModelType>())}"
				};
			}

			try
			{
				var result = await _autoTrainingService.TrainModelAsync(type);

				return new ModelTrainingResultDto
				{
					Success = result.Success,
					RunId = result.RunId,
					Metrics = result.Metrics?.ToDictionary(),
					PromotedToProduction = result.PromotedToProduction,
					Message = result.Message,
					PreviousProductionRunId = result.PreviousProductionRunId
				};
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to train model: {ModelType}", modelType);
				return new ModelTrainingResultDto
				{
					Success = false,
					Message = $"Failed to train {modelType}: {ex.Message}"
				};
			}
		}

		public async Task<List<ModelVersionDto>> GetModelVersionsAsync(string modelType)
		{
			if (!Enum.TryParse<ModelType>(modelType, true, out var type))
			{
				return new List<ModelVersionDto>();
			}

			var versions = await _trainingService.GetModelVersionsAsync(type);
			return versions.Select(v => new ModelVersionDto
			{
				RunId = v.RunId,
				Version = v.Version,
				Stage = v.Stage,
				CreatedAt = v.CreatedAt,
				Metrics = v.Metrics?.ToDictionary(),
				ComparisonResult = v.ComparisonResult,
				PreviousProductionRunId = v.PreviousProductionRunId
			}).ToList();
		}

		public async Task<ModelVersionDto?> GetProductionModelAsync(string modelType)
		{
			if (!Enum.TryParse<ModelType>(modelType, true, out var type))
			{
				return null;
			}

			var modelName = type switch
			{
				ModelType.JobRecommendation => ModelTrainingService.JobRecommendationModel,
				ModelType.StudentRecommendation => ModelTrainingService.StudentRecommendationModel,
				ModelType.JobPayPrediction => ModelTrainingService.JobPayPredictionModel,
				_ => throw new ArgumentException($"Unknown model type: {type}")
			};

			var productionVersion = await _mlflowClient.GetProductionModelVersionAsync(modelName);
			if (productionVersion == null) return null;

			var run = await _mlflowClient.GetRunAsync(productionVersion.RunId!);
			var comparisonResult = run?.Data?.Params?.FirstOrDefault(p => p.Key == "comparison_result")?.Value;
			var previousRunId = run?.Data?.Params?.FirstOrDefault(p => p.Key == "previous_production_run_id")?.Value;

			return new ModelVersionDto
			{
				RunId = productionVersion.RunId,
				Version = productionVersion.Version,
				Stage = productionVersion.CurrentStage ?? "Production",
				CreatedAt = productionVersion.CreationTimestamp.HasValue
					? DateTimeOffset.FromUnixTimeMilliseconds(productionVersion.CreationTimestamp.Value).DateTime
					: DateTime.MinValue,
				Metrics = run?.Data?.Metrics?.ToDictionary(m => m.Key ?? "", m => m.Value),
				ComparisonResult = comparisonResult,
				PreviousProductionRunId = previousRunId
			};
		}

		public async Task<bool> PromoteToProductionAsync(string modelType, string version)
		{
			if (!Enum.TryParse<ModelType>(modelType, true, out var type))
			{
				return false;
			}

			var modelName = type switch
			{
				ModelType.JobRecommendation => ModelTrainingService.JobRecommendationModel,
				ModelType.StudentRecommendation => ModelTrainingService.StudentRecommendationModel,
				ModelType.JobPayPrediction => ModelTrainingService.JobPayPredictionModel,
				_ => throw new ArgumentException($"Unknown model type: {type}")
			};

			try
			{
				await _mlflowClient.TransitionModelVersionStageAsync(modelName, version, "Production");
				_logger.LogInformation("Promoted model {ModelName} version {Version} to production", modelName, version);
				
				// Invalidate cache for this model type
				_trainingService.InvalidateModelCache(type);
				
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to promote model {ModelName} version {Version}", modelName, version);
				return false;
			}
		}

		public async Task<bool> RollbackProductionModelAsync(string modelType)
		{
			if (!Enum.TryParse<ModelType>(modelType, true, out var type))
			{
				return false;
			}

			var modelName = type switch
			{
				ModelType.JobRecommendation => ModelTrainingService.JobRecommendationModel,
				ModelType.StudentRecommendation => ModelTrainingService.StudentRecommendationModel,
				ModelType.JobPayPrediction => ModelTrainingService.JobPayPredictionModel,
				_ => throw new ArgumentException($"Unknown model type: {type}")
			};

			try
			{
				// Get current production version
				var productionVersion = await _mlflowClient.GetProductionModelVersionAsync(modelName);
				if (productionVersion == null)
				{
					_logger.LogWarning("No production model to rollback for: {ModelName}", modelName);
					return false;
				}

				// Get all versions to find the previous one
				var allVersions = await _mlflowClient.GetLatestModelVersionsAsync(modelName);
				var archivedVersions = allVersions
					.Where(v => v.CurrentStage == "Archived" && v.Version != productionVersion.Version)
					.OrderByDescending(v => v.CreationTimestamp)
					.ToList();

				if (archivedVersions.Count == 0)
				{
					_logger.LogWarning("No archived versions available for rollback: {ModelName}", modelName);
					return false;
				}

				var previousVersion = archivedVersions.First();

				// Demote current production to staging
				await _mlflowClient.TransitionModelVersionStageAsync(modelName, productionVersion.Version!, "Staging");

				// Promote previous version to production
				await _mlflowClient.TransitionModelVersionStageAsync(modelName, previousVersion.Version!, "Production");

				_logger.LogInformation("Rolled back model {ModelName} from version {CurrentVersion} to {PreviousVersion}",
					modelName, productionVersion.Version, previousVersion.Version);

				// Invalidate cache
				_trainingService.InvalidateModelCache(type);

				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to rollback model: {ModelName}", modelName);
				return false;
			}
		}

		public TrainingStatusDto GetTrainingStatus()
		{
			var status = _autoTrainingService.GetTrainingStatus();
			return new TrainingStatusDto
			{
				IsTraining = status.IsTraining,
				IsEnabled = status.IsEnabled,
				ScheduledTime = status.ScheduledTime.ToString(@"hh\:mm"),
				NextRunTime = status.NextRunTime
			};
		}

		public void InvalidateModelCache(string? modelType = null)
		{
			if (string.IsNullOrEmpty(modelType))
			{
				_trainingService.InvalidateAllModelCaches();
				_logger.LogInformation("Invalidated all model caches");
			}
			else if (Enum.TryParse<ModelType>(modelType, true, out var type))
			{
				_trainingService.InvalidateModelCache(type);
				_logger.LogInformation("Invalidated cache for model type: {ModelType}", modelType);
			}
		}
	}
}
