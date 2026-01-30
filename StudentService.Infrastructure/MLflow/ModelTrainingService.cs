using Microsoft.ML;
using Microsoft.Extensions.Logging;
using StudentService.Infrastructure.Data;
using System.Collections.Concurrent;

namespace StudentService.Infrastructure.MLflow
{
	public class ModelTrainingService
	{
		private readonly MLflowClient _mlflowClient;
		private readonly MongoDbContext _dbContext;
		private readonly ILogger<ModelTrainingService> _logger;
		private readonly string _modelStoragePath;

		private static readonly ConcurrentDictionary<ModelType, CachedModel> _modelCache = new();
		private static readonly TimeSpan ModelCacheExpiry = TimeSpan.FromHours(1);

		public const double MinimumImprovementDelta = 0.05;

		public const string JobRecommendationModel = "job-recommendation-model";
		public const string StudentRecommendationModel = "student-recommendation-model";
		public const string JobPayPredictionModel = "job-pay-prediction-model";

		public const string JobRecommendationExperiment = "job-recommendation-experiment";
		public const string StudentRecommendationExperiment = "student-recommendation-experiment-v2";
		public const string JobPayPredictionExperiment = "job-pay-prediction-experiment";

		public ModelTrainingService(
			MLflowClient mlflowClient,
			MongoDbContext dbContext,
			ILogger<ModelTrainingService> logger,
			string modelStoragePath = "models")
		{
			_mlflowClient = mlflowClient;
			_dbContext = dbContext;
			_logger = logger;
			_modelStoragePath = modelStoragePath;

			Directory.CreateDirectory(_modelStoragePath);
		}

		public async Task<ModelTrainingResult> TrainAndEvaluateAsync(
			ModelType modelType,
			Func<MLContext, (ITransformer Model, ModelMetrics Metrics)> trainFunc)
		{
			var experimentName = GetExperimentName(modelType);
			var modelName = GetModelName(modelType);
			
			_logger.LogInformation("Starting training for model: {ModelName}", modelName);

			var experimentId = await _mlflowClient.GetOrCreateExperimentAsync(experimentName);

			var currentProdVersion = await _mlflowClient.GetProductionModelVersionAsync(modelName);
			var currentProdRunId = currentProdVersion?.RunId;

			var runName = $"{modelName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
			var tags = new Dictionary<string, string>
			{
				["model_type"] = modelType.ToString(),
				["training_date"] = DateTime.UtcNow.ToString("O"),
				["framework"] = "ML.NET",
				["comparison_metric"] = modelType == ModelType.JobPayPrediction ? "MAE" : "PR-AUC"
			};

			if (!string.IsNullOrEmpty(currentProdRunId))
			{
				tags["previous_production_run_id"] = currentProdRunId;
				tags["previous_production_version"] = currentProdVersion?.Version ?? "unknown";
			}

			var run = await _mlflowClient.CreateRunAsync(experimentId, runName, tags);
			var runId = run.Info?.RunId ?? throw new Exception("Failed to get run ID");

			try
			{
				var seed = Environment.TickCount ^ DateTime.UtcNow.Millisecond;
				var mlContext = new MLContext(seed: seed);
				var (model, metrics) = trainFunc(mlContext);

				var parameters = new Dictionary<string, string>
				{
					["model_type"] = modelType.ToString(),
					["framework"] = "ML.NET",
					["seed"] = seed.ToString(),
					["minimum_improvement_delta"] = MinimumImprovementDelta.ToString("F4")
				};

				if (!string.IsNullOrEmpty(currentProdRunId))
				{
					parameters["previous_production_run_id"] = currentProdRunId;
					parameters["previous_production_version"] = currentProdVersion?.Version ?? "unknown";
				}

				await _mlflowClient.LogParamsAsync(runId, parameters);

				await _mlflowClient.LogMetricsAsync(runId, metrics.ToDictionary());

				var currentProdMetrics = await GetProductionModelMetricsAsync(modelName);
				var (isBetter, comparisonResult) = IsModelBetter(metrics, currentProdMetrics, modelType);

				await _mlflowClient.LogParamAsync(runId, "is_better_than_production", isBetter.ToString());
				await _mlflowClient.LogParamAsync(runId, "comparison_result", comparisonResult);

				_logger.LogInformation(
					"Model {ModelName} trained. New metrics: {NewMetrics}, Current production: {ProdMetrics}, Is better: {IsBetter}, Comparison: {Comparison}",
					modelName, metrics, currentProdMetrics, isBetter, comparisonResult);

				var modelPath = Path.Combine(_modelStoragePath, $"{modelName}-{runId}.zip");
				mlContext.Model.Save(model, null, modelPath);
				await _mlflowClient.LogArtifactAsync(runId, modelPath, "model");

				await _mlflowClient.FinishRunAsync(runId);

				if (isBetter)
				{
					await RegisterAndPromoteModelAsync(modelName, runId, metrics, currentProdVersion);
					_modelCache.TryRemove(modelType, out _);
					return new ModelTrainingResult
					{
						Success = true,
						RunId = runId,
						Metrics = metrics,
						PromotedToProduction = true,
						PreviousProductionRunId = currentProdRunId,
						Message = $"New model version promoted to production. {comparisonResult}"
					};
				}

				return new ModelTrainingResult
				{
					Success = true,
					RunId = runId,
					Metrics = metrics,
					PromotedToProduction = false,
					PreviousProductionRunId = currentProdRunId,
					Message = $"Model trained but not promoted. {comparisonResult}"
				};
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Training failed for model: {ModelName}", modelName);
				await _mlflowClient.FinishRunAsync(runId, "FAILED");
				
				return new ModelTrainingResult
				{
					Success = false,
					RunId = runId,
					Message = $"Training failed: {ex.Message}"
				};
			}
		}

		private async Task<ModelMetrics?> GetProductionModelMetricsAsync(string modelName)
		{
			try
			{
				var productionVersion = await _mlflowClient.GetProductionModelVersionAsync(modelName);
				if (productionVersion?.RunId == null) return null;

				var run = await _mlflowClient.GetRunAsync(productionVersion.RunId);
				if (run?.Data?.Metrics == null) return null;

				return ModelMetrics.FromDictionary(
					run.Data.Metrics.ToDictionary(m => m.Key ?? "", m => m.Value));
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to get production model metrics for: {ModelName}", modelName);
				return null;
			}
		}

		private (bool IsBetter, string ComparisonResult) IsModelBetter(ModelMetrics newMetrics, ModelMetrics? currentMetrics, ModelType modelType)
		{
			if (currentMetrics == null)
			{
				return (true, "No existing production model - new model automatically promoted");
			}

			return modelType switch
			{
				ModelType.JobRecommendation or ModelType.StudentRecommendation =>
					CompareRecommendationMetrics(newMetrics, currentMetrics),

				ModelType.JobPayPrediction =>
					CompareRegressionMetrics(newMetrics, currentMetrics),

				_ => (false, "Unknown model type")
			};
		}

		private (bool IsBetter, string ComparisonResult) CompareRecommendationMetrics(ModelMetrics newMetrics, ModelMetrics currentMetrics)
		{
			var newPrimaryMetric = newMetrics.PRAUC > 0 ? newMetrics.PRAUC : newMetrics.AUC;
			var currentPrimaryMetric = currentMetrics.PRAUC > 0 ? currentMetrics.PRAUC : currentMetrics.AUC;

			var improvement = newPrimaryMetric - currentPrimaryMetric;
			var relativeImprovement = currentPrimaryMetric > 0 
				? improvement / currentPrimaryMetric 
				: (newPrimaryMetric > 0 ? 1.0 : 0.0);

			var comparisonDetail = $"PR-AUC: {newPrimaryMetric:F4} vs {currentPrimaryMetric:F4} (improvement: {improvement:F4}, relative: {relativeImprovement:P2})";

			if (relativeImprovement >= MinimumImprovementDelta)
			{
				return (true, $"Model improved. {comparisonDetail}");
			}

			if (Math.Abs(improvement) < 0.01 && newMetrics.F1Score > currentMetrics.F1Score * (1 + MinimumImprovementDelta))
			{
				return (true, $"Model improved on secondary metric (F1). {comparisonDetail}, F1: {newMetrics.F1Score:F4} vs {currentMetrics.F1Score:F4}");
			}

			return (false, $"Model did not meet minimum improvement threshold ({MinimumImprovementDelta:P0}). {comparisonDetail}");
		}

		private (bool IsBetter, string ComparisonResult) CompareRegressionMetrics(ModelMetrics newMetrics, ModelMetrics currentMetrics)
		{
			var improvement = currentMetrics.MAE - newMetrics.MAE;
			var relativeImprovement = currentMetrics.MAE > 0 
				? improvement / currentMetrics.MAE 
				: (newMetrics.MAE < currentMetrics.MAE ? 1.0 : 0.0);

			var comparisonDetail = $"MAE: {newMetrics.MAE:F4} vs {currentMetrics.MAE:F4} (improvement: {improvement:F4}, relative: {relativeImprovement:P2})";

			if (relativeImprovement >= MinimumImprovementDelta)
			{
				return (true, $"Model improved. {comparisonDetail}");
			}

			if (Math.Abs(improvement) < 0.01 && newMetrics.RMSE < currentMetrics.RMSE * (1 - MinimumImprovementDelta))
			{
				return (true, $"Model improved on secondary metric (RMSE). {comparisonDetail}, RMSE: {newMetrics.RMSE:F4} vs {currentMetrics.RMSE:F4}");
			}

			return (false, $"Model did not meet minimum improvement threshold ({MinimumImprovementDelta:P0}). {comparisonDetail}");
		}

		private async Task RegisterAndPromoteModelAsync(string modelName, string runId, ModelMetrics metrics, ModelVersion? previousVersion)
		{
			try
			{
				await _mlflowClient.CreateRegisteredModelAsync(modelName, 
					$"Auto-trained model for {modelName}");

				var description = $"Trained on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}. Metrics: {metrics}";
				if (previousVersion != null)
				{
					description += $". Replaced version {previousVersion.Version} (run: {previousVersion.RunId})";
				}

				var version = await _mlflowClient.CreateModelVersionAsync(
					modelName, 
					runId,
					description);

				await _mlflowClient.TransitionModelVersionStageAsync(
					modelName, 
					version.Version ?? "1", 
					"Production");

				_logger.LogInformation(
					"Model {ModelName} version {Version} promoted to production (replaced version {PreviousVersion})",
					modelName, version.Version, previousVersion?.Version ?? "none");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to register and promote model: {ModelName}", modelName);
				throw;
			}
		}

		public async Task<ITransformer?> LoadProductionModelAsync(ModelType modelType, MLContext mlContext)
		{
			var modelName = GetModelName(modelType);

			if (_modelCache.TryGetValue(modelType, out var cachedModel) && 
			    cachedModel.ExpiresAt > DateTime.UtcNow)
			{
				_logger.LogDebug("Using cached model for: {ModelName}", modelName);
				return cachedModel.Model;
			}

			try
			{
				var productionVersion = await _mlflowClient.GetProductionModelVersionAsync(modelName);
				if (productionVersion?.RunId == null)
				{
					_logger.LogWarning("No production model found for: {ModelName}", modelName);
					return null;
				}

				var modelPath = await _mlflowClient.GetModelArtifactPathAsync(modelName);
				
				if (string.IsNullOrEmpty(modelPath))
				{
					var run = await _mlflowClient.GetRunAsync(productionVersion.RunId);
					modelPath = run?.Data?.Params?.FirstOrDefault(p => p.Key == "model_local_path")?.Value;
				}

				if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
				{
					var downloadPath = Path.Combine(_modelStoragePath, $"{modelName}-{productionVersion.RunId}.zip");
					modelPath = await _mlflowClient.DownloadArtifactAsync(
						productionVersion.RunId, 
						"model", 
						downloadPath);
				}

				if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
				{
					_logger.LogWarning("Production model file not found for: {ModelName}", modelName);
					return null;
				}

				var model = mlContext.Model.Load(modelPath, out _);
				
				_modelCache[modelType] = new CachedModel
				{
					Model = model,
					ExpiresAt = DateTime.UtcNow.Add(ModelCacheExpiry),
					Version = productionVersion.Version
				};

				_logger.LogInformation("Loaded production model: {ModelName} version {Version} from {Path}", 
					modelName, productionVersion.Version, modelPath);
				
				return model;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to load production model: {ModelName}", modelName);
				return null;
			}
		}

		public void InvalidateModelCache(ModelType modelType)
		{
			_modelCache.TryRemove(modelType, out _);
			_logger.LogInformation("Invalidated cache for model type: {ModelType}", modelType);
		}

		public void InvalidateAllModelCaches()
		{
			_modelCache.Clear();
			_logger.LogInformation("Invalidated all model caches");
		}

		public async Task<List<ModelVersionInfo>> GetModelVersionsAsync(ModelType modelType)
		{
			var modelName = GetModelName(modelType);
			var experimentName = GetExperimentName(modelType);

			try
			{
				var experimentId = await _mlflowClient.GetOrCreateExperimentAsync(experimentName);
				var runs = await _mlflowClient.SearchRunsAsync(experimentId);
				var registeredModel = await _mlflowClient.GetRegisteredModelAsync(modelName);

				var versions = new List<ModelVersionInfo>();
				
				foreach (var run in runs)
				{
					if (run.Info?.RunId == null) continue;

					var version = registeredModel?.LatestVersions?
						.FirstOrDefault(v => v.RunId == run.Info.RunId);

					var comparisonResult = run.Data?.Params?.FirstOrDefault(p => p.Key == "comparison_result")?.Value;
					var previousRunId = run.Data?.Params?.FirstOrDefault(p => p.Key == "previous_production_run_id")?.Value;

					versions.Add(new ModelVersionInfo
					{
						RunId = run.Info.RunId,
						Version = version?.Version,
						Stage = version?.CurrentStage ?? "None",
						CreatedAt = run.Info.StartTime.HasValue 
							? DateTimeOffset.FromUnixTimeMilliseconds(run.Info.StartTime.Value).DateTime 
							: DateTime.MinValue,
						Metrics = run.Data?.Metrics != null 
							? ModelMetrics.FromDictionary(run.Data.Metrics.ToDictionary(m => m.Key ?? "", m => m.Value))
							: null,
						ComparisonResult = comparisonResult,
						PreviousProductionRunId = previousRunId
					});
				}

				return versions.OrderByDescending(v => v.CreatedAt).ToList();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to get model versions for: {ModelName}", modelName);
				return new List<ModelVersionInfo>();
			}
		}

		public async Task<bool> HasProductionModelAsync(ModelType modelType)
		{
			var modelName = GetModelName(modelType);
			var productionVersion = await _mlflowClient.GetProductionModelVersionAsync(modelName);
			return productionVersion != null;
		}

		public static string GetExperimentName(ModelType modelType) => modelType switch
		{
			ModelType.JobRecommendation => JobRecommendationExperiment,
			ModelType.StudentRecommendation => StudentRecommendationExperiment,
			ModelType.JobPayPrediction => JobPayPredictionExperiment,
			_ => throw new ArgumentException($"Unknown model type: {modelType}")
		};

		public static string GetModelName(ModelType modelType) => modelType switch
		{
			ModelType.JobRecommendation => JobRecommendationModel,
			ModelType.StudentRecommendation => StudentRecommendationModel,
			ModelType.JobPayPrediction => JobPayPredictionModel,
			_ => throw new ArgumentException($"Unknown model type: {modelType}")
		};
	}

	internal class CachedModel
	{
		public ITransformer? Model { get; set; }
		public DateTime ExpiresAt { get; set; }
		public string? Version { get; set; }
	}

	public enum ModelType
	{
		JobRecommendation,
		StudentRecommendation,
		JobPayPrediction
	}

	public class ModelMetrics
	{
		public double Accuracy { get; set; }
		public double AUC { get; set; }
		public double PRAUC { get; set; }
		public double Precision { get; set; }
		public double Recall { get; set; }
		public double F1Score { get; set; }
		public double MAE { get; set; }
		public double MSE { get; set; }
		public double RMSE { get; set; }
		public double RSquared { get; set; }
		public double NDCGAt10 { get; set; }

		public Dictionary<string, double> ToDictionary() => new()
		{
			["accuracy"] = Accuracy,
			["auc"] = AUC,
			["pr_auc"] = PRAUC,
			["precision"] = Precision,
			["recall"] = Recall,
			["f1_score"] = F1Score,
			["mae"] = MAE,
			["mse"] = MSE,
			["rmse"] = RMSE,
			["r_squared"] = RSquared,
			["ndcg_at_10"] = NDCGAt10
		};

		public static ModelMetrics FromDictionary(Dictionary<string, double> dict) => new()
		{
			Accuracy = dict.GetValueOrDefault("accuracy"),
			AUC = dict.GetValueOrDefault("auc"),
			PRAUC = dict.GetValueOrDefault("pr_auc"),
			Precision = dict.GetValueOrDefault("precision"),
			Recall = dict.GetValueOrDefault("recall"),
			F1Score = dict.GetValueOrDefault("f1_score"),
			MAE = dict.GetValueOrDefault("mae"),
			MSE = dict.GetValueOrDefault("mse"),
			RMSE = dict.GetValueOrDefault("rmse"),
			RSquared = dict.GetValueOrDefault("r_squared"),
			NDCGAt10 = dict.GetValueOrDefault("ndcg_at_10")
		};

		public override string ToString() => 
			$"Accuracy={Accuracy:F4}, AUC={AUC:F4}, PR-AUC={PRAUC:F4}, F1={F1Score:F4}, MAE={MAE:F4}, RMSE={RMSE:F4}";
	}

	public class ModelTrainingResult
	{
		public bool Success { get; set; }
		public string? RunId { get; set; }
		public ModelMetrics? Metrics { get; set; }
		public bool PromotedToProduction { get; set; }
		public string? Message { get; set; }
		public string? PreviousProductionRunId { get; set; }
	}

	public class ModelVersionInfo
	{
		public string? RunId { get; set; }
		public string? Version { get; set; }
		public string Stage { get; set; } = "None";
		public DateTime CreatedAt { get; set; }
		public ModelMetrics? Metrics { get; set; }
		public string? ComparisonResult { get; set; }
		public string? PreviousProductionRunId { get; set; }
	}
}
