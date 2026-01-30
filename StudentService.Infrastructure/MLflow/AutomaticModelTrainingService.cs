using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML;
using Microsoft.ML.Trainers;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;
using StudentService.Infrastructure.Services.Models;

namespace StudentService.Infrastructure.MLflow
{
	public class AutomaticModelTrainingService : BackgroundService
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly ILogger<AutomaticModelTrainingService> _logger;
		private readonly TimeSpan _scheduledTime;
		private readonly bool _enabled;
		private bool _isTraining;

		public AutomaticModelTrainingService(
			IServiceProvider serviceProvider,
			ILogger<AutomaticModelTrainingService> logger,
			TimeSpan? scheduledTime = null,
			bool enabled = true)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
			_scheduledTime = scheduledTime ?? new TimeSpan(22, 0, 0); 
			_enabled = enabled;
			_isTraining = false;
		}

		public bool IsTraining => _isTraining;
		public TimeSpan ScheduledTime => _scheduledTime;
		public bool IsEnabled => _enabled;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_enabled)
			{
				_logger.LogInformation("Automatic model training is disabled");
				return;
			}

			_logger.LogInformation("Automatic model training service started. Scheduled time: {ScheduledTime}", _scheduledTime);

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					var delay = CalculateDelayUntilNextRun();
					_logger.LogInformation("Next model training scheduled in {Delay} at {NextRunTime}", 
						delay, DateTime.Now.Add(delay));

					await Task.Delay(delay, stoppingToken);

					if (!stoppingToken.IsCancellationRequested)
					{
						await TrainAllModelsAsync(stoppingToken);
					}
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error during automatic model training");
					await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
				}
			}
		}

		private TimeSpan CalculateDelayUntilNextRun()
		{
			var now = DateTime.Now;
			var scheduledToday = now.Date.Add(_scheduledTime);
			if (now >= scheduledToday)
			{
				scheduledToday = scheduledToday.AddDays(1);
			}
			return scheduledToday - now;
		}

		public async Task<List<ModelTrainingResult>> TrainAllModelsAsync(CancellationToken cancellationToken = default)
		{
			if (_isTraining)
			{
				_logger.LogWarning("Training already in progress, skipping");
				return new List<ModelTrainingResult> { new() { Success = false, Message = "Training already in progress" } };
			}

			_isTraining = true;
			var results = new List<ModelTrainingResult>();

			try
			{
				_logger.LogInformation("Starting automatic training for all models at {Time}", DateTime.UtcNow);

				using var scope = _serviceProvider.CreateScope();
				var trainingService = scope.ServiceProvider.GetRequiredService<ModelTrainingService>();
				var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

				results.Add(await TrainJobRecommendationModelAsync(trainingService, dbContext, cancellationToken));
				results.Add(await TrainStudentRecommendationModelAsync(trainingService, dbContext, cancellationToken));
				results.Add(await TrainJobPayPredictionModelAsync(trainingService, dbContext, cancellationToken));

				_logger.LogInformation("Completed automatic training. Results: {PromotedCount}/{TotalCount} promoted",
					results.Count(r => r.PromotedToProduction), results.Count);

				return results;
			}
			finally
			{
				_isTraining = false;
			}
		}

		public async Task<ModelTrainingResult> TrainModelAsync(ModelType modelType, CancellationToken cancellationToken = default)
		{
			if (_isTraining)
			{
				return new ModelTrainingResult { Success = false, Message = "Training already in progress" };
			}

			_isTraining = true;

			try
			{
				using var scope = _serviceProvider.CreateScope();
				var trainingService = scope.ServiceProvider.GetRequiredService<ModelTrainingService>();
				var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

				var result = modelType switch
				{
					ModelType.JobRecommendation => await TrainJobRecommendationModelAsync(trainingService, dbContext, cancellationToken),
					ModelType.StudentRecommendation => await TrainStudentRecommendationModelAsync(trainingService, dbContext, cancellationToken),
					ModelType.JobPayPrediction => await TrainJobPayPredictionModelAsync(trainingService, dbContext, cancellationToken),
					_ => throw new ArgumentException($"Unknown model type: {modelType}")
				};

				_logger.LogInformation("Training result for {ModelType}: {Result}", modelType, result.Message);
				return result;
			}
			finally
			{
				_isTraining = false;
			}
		}

		public TrainingStatus GetTrainingStatus()
		{
			return new TrainingStatus
			{
				IsTraining = _isTraining,
				IsEnabled = _enabled,
				ScheduledTime = _scheduledTime,
				NextRunTime = DateTime.Now.Add(CalculateDelayUntilNextRun())
			};
		}

		private async Task<ModelTrainingResult> TrainJobRecommendationModelAsync(
			ModelTrainingService trainingService, MongoDbContext dbContext, CancellationToken cancellationToken)
		{
			_logger.LogInformation("Training Job Recommendation Model...");

			return await trainingService.TrainAndEvaluateAsync(ModelType.JobRecommendation, mlContext =>
			{
				var (samples, _) = PrepareJobRecommendationData(dbContext);
				if (samples.Count < 10)
					throw new InvalidOperationException("Insufficient training data for Job Recommendation model");

				var dataView = mlContext.Data.LoadFromEnumerable(samples);
				var split = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

				var pipeline = mlContext.Transforms.Conversion
					.MapValueToKey("studentKeyIndex", nameof(JobHybridInput.StudentKey))
					.Append(mlContext.Transforms.Categorical.OneHotEncoding("studentKeyVec", "studentKeyIndex"))
					.Append(mlContext.Transforms.Conversion.MapValueToKey("jobKeyIndex", nameof(JobHybridInput.JobKey)))
					.Append(mlContext.Transforms.Categorical.OneHotEncoding("jobKeyVec", "jobKeyIndex"))
					.Append(mlContext.Transforms.Concatenate("Features", "studentKeyVec", "jobKeyVec",
						nameof(JobHybridInput.CompanyRating), nameof(JobHybridInput.HourlyPay),
						nameof(JobHybridInput.JobTypeExperience), nameof(JobHybridInput.TraitMatchScore)))
					.Append(mlContext.BinaryClassification.Trainers.FieldAwareFactorizationMachine(
						new FieldAwareFactorizationMachineTrainer.Options
						{
							FeatureColumnName = "Features",
							LabelColumnName = nameof(JobHybridInput.Label),
							LatentDimension = 16,
							NumberOfIterations = 50,
							LearningRate = 0.2f,
							LambdaLinear = 0.0001f,
							LambdaLatent = 0.0001f,
							NormalizeFeatures = true,
							Shuffle = true
						}));

				var model = pipeline.Fit(split.TrainSet);
				var predictions = model.Transform(split.TestSet);
				var metrics = mlContext.BinaryClassification.Evaluate(predictions, nameof(JobHybridInput.Label));
				var nonCalibratedMetrics = mlContext.BinaryClassification.EvaluateNonCalibrated(predictions, nameof(JobHybridInput.Label));
				var prAuc = nonCalibratedMetrics.AreaUnderPrecisionRecallCurve;
				var ndcgAt10 = CalculateNDCGAt10ForBinaryClassification(mlContext, predictions, nameof(JobHybridInput.Label));

				_logger.LogInformation("JobRecommendation metrics - Accuracy: {Accuracy:F4}, AUC: {AUC:F4}, PR-AUC: {PRAUC:F4}, NDCG@10: {NDCG:F4}",
					metrics.Accuracy, metrics.AreaUnderRocCurve, prAuc, ndcgAt10);

				return (model, new ModelMetrics
				{
					Accuracy = metrics.Accuracy,
					AUC = metrics.AreaUnderRocCurve,
					PRAUC = prAuc,
					Precision = metrics.PositivePrecision,
					Recall = metrics.PositiveRecall,
					F1Score = metrics.F1Score,
					NDCGAt10 = ndcgAt10
				});
			});
		}

		private async Task<ModelTrainingResult> TrainStudentRecommendationModelAsync(
			ModelTrainingService trainingService, MongoDbContext dbContext, CancellationToken cancellationToken)
		{
			_logger.LogInformation("Training Student Recommendation Model...");

			return await trainingService.TrainAndEvaluateAsync(ModelType.StudentRecommendation, mlContext =>
			{
				var (samples, _) = PrepareStudentRecommendationData(dbContext);
				if (samples.Count < 10)
					throw new InvalidOperationException("Insufficient training data for Student Recommendation model");

				var dataView = mlContext.Data.LoadFromEnumerable(samples);
				var split = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

				var pipeline = mlContext.Transforms.Conversion
					.MapValueToKey("studentKeyIndex", nameof(HybridInput.StudentKey))
					.Append(mlContext.Transforms.Categorical.OneHotEncoding("studentKeyVec", "studentKeyIndex"))
					.Append(mlContext.Transforms.Conversion.MapValueToKey("jobKeyIndex", nameof(HybridInput.JobKey)))
					.Append(mlContext.Transforms.Categorical.OneHotEncoding("jobKeyVec", "jobKeyIndex"))
					.Append(mlContext.Transforms.Concatenate("Features", "studentKeyVec", "jobKeyVec",
						nameof(HybridInput.JobTypeExperience), nameof(HybridInput.TraitScore), nameof(HybridInput.StudentRating)))
					.Append(mlContext.BinaryClassification.Trainers.FieldAwareFactorizationMachine(
						new FieldAwareFactorizationMachineTrainer.Options
						{
							FeatureColumnName = "Features",
							LabelColumnName = nameof(HybridInput.Label),
							LatentDimension = 16,
							NumberOfIterations = 50,
							LearningRate = 0.2f,
							NormalizeFeatures = true,
							Shuffle = true
						}));

				var model = pipeline.Fit(split.TrainSet);
				var predictions = model.Transform(split.TestSet);
				var metrics = mlContext.BinaryClassification.Evaluate(predictions, nameof(HybridInput.Label));
				var nonCalibratedMetrics = mlContext.BinaryClassification.EvaluateNonCalibrated(predictions, nameof(HybridInput.Label));
				var prAuc = nonCalibratedMetrics.AreaUnderPrecisionRecallCurve;
				var ndcgAt10 = CalculateNDCGAt10ForBinaryClassification(mlContext, predictions, nameof(HybridInput.Label));

				_logger.LogInformation("StudentRecommendation metrics - Accuracy: {Accuracy:F4}, AUC: {AUC:F4}, PR-AUC: {PRAUC:F4}, NDCG@10: {NDCG:F4}",
					metrics.Accuracy, metrics.AreaUnderRocCurve, prAuc, ndcgAt10);

				return (model, new ModelMetrics
				{
					Accuracy = metrics.Accuracy,
					AUC = metrics.AreaUnderRocCurve,
					PRAUC = prAuc,
					Precision = metrics.PositivePrecision,
					Recall = metrics.PositiveRecall,
					F1Score = metrics.F1Score,
					NDCGAt10 = ndcgAt10
				});
			});
		}

		private async Task<ModelTrainingResult> TrainJobPayPredictionModelAsync(
			ModelTrainingService trainingService, MongoDbContext dbContext, CancellationToken cancellationToken)
		{
			_logger.LogInformation("Training Job Pay Prediction Model...");

			return await trainingService.TrainAndEvaluateAsync(ModelType.JobPayPrediction, mlContext =>
			{
				var jobs = dbContext.GetCollection<Job>("jobs").Find(job => job.HourlyPay <= 10f).ToList();

				var data = jobs.Select(job => new JobPayData
				{
					Type = job.Type ?? string.Empty,
					PlaceOfWork = job.PlaceOfWork ?? string.Empty,
					RequiredTraits = job.RequiredTraits != null ? string.Join(",", job.RequiredTraits) : string.Empty,
					HourlyPay = (float)job.HourlyPay
				}).ToList();

				if (data.Count < 10)
					throw new InvalidOperationException("Insufficient training data for Job Pay Prediction model");

				var dataView = mlContext.Data.LoadFromEnumerable(data);
				var split = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.3);

				var pipeline = mlContext.Transforms.Categorical
					.OneHotEncoding("TypeEncoded", nameof(JobPayData.Type))
					.Append(mlContext.Transforms.Categorical.OneHotEncoding("PlaceOfWorkEncoded", nameof(JobPayData.PlaceOfWork)))
					.Append(mlContext.Transforms.Text.FeaturizeText("TraitsFeatures", nameof(JobPayData.RequiredTraits)))
					.Append(mlContext.Transforms.Concatenate("Features", "TypeEncoded", "PlaceOfWorkEncoded", "TraitsFeatures"))
					.Append(mlContext.Regression.Trainers.FastTree(nameof(JobPayData.HourlyPay), "Features"));

				var model = pipeline.Fit(split.TrainSet);
				var predictions = model.Transform(split.TestSet);
				var metrics = mlContext.Regression.Evaluate(predictions, nameof(JobPayData.HourlyPay), "Score");

				_logger.LogInformation("JobPayPrediction metrics - MAE: {MAE:F4}, RMSE: {RMSE:F4}, R?: {R2:F4}",
					metrics.MeanAbsoluteError, metrics.RootMeanSquaredError, metrics.RSquared);

				return (model, new ModelMetrics
				{
					MAE = metrics.MeanAbsoluteError,
					MSE = metrics.MeanSquaredError,
					RMSE = metrics.RootMeanSquaredError,
					RSquared = metrics.RSquared
				});
			});
		}

		private double CalculateNDCGAt10ForBinaryClassification(MLContext mlContext, IDataView predictions, string labelColumnName)
		{
			try
			{
				var schema = predictions.Schema;
				
				DataViewSchema.Column? scoreColumn = null;
				foreach (var col in schema)
				{
					if (col.Name == "Score")
					{
						scoreColumn = col;
						break;
					}
				}

				if (scoreColumn == null)
				{
					_logger.LogWarning("Score column not found in predictions for NDCG calculation");
					return 0;
				}

				var predictionData = new List<(bool Label, float Score)>();
				
				using (var cursor = predictions.GetRowCursor(new[] { schema[labelColumnName], schema["Score"] }))
				{
					var labelGetter = cursor.GetGetter<bool>(schema[labelColumnName]);
					var scoreGetter = cursor.GetGetter<float>(schema["Score"]);

					while (cursor.MoveNext())
					{
						bool label = false;
						float score = 0f;
						labelGetter(ref label);
						scoreGetter(ref score);
						predictionData.Add((label, score));
					}
				}

				if (predictionData.Count == 0)
				{
					_logger.LogWarning("No prediction data available for NDCG calculation");
					return 0;
				}

				var rankedPredictions = predictionData.OrderByDescending(p => p.Score).Take(10).ToList();

				double dcg = 0;
				for (int i = 0; i < rankedPredictions.Count; i++)
				{
					var relevance = rankedPredictions[i].Label ? 1.0 : 0.0;
					dcg += relevance / Math.Log2(i + 2);
				}

				var idealRanking = predictionData.OrderByDescending(p => p.Label).ThenByDescending(p => p.Score).Take(10).ToList();

				double idcg = 0;
				for (int i = 0; i < idealRanking.Count; i++)
				{
					var relevance = idealRanking[i].Label ? 1.0 : 0.0;
					idcg += relevance / Math.Log2(i + 2);
				}

				var ndcg = idcg > 0 ? dcg / idcg : 0;

				_logger.LogDebug("NDCG@10: DCG={DCG:F4}, IDCG={IDCG:F4}, NDCG={NDCG:F4}", dcg, idcg, ndcg);

				return ndcg;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to calculate NDCG@10, returning 0");
				return 0;
			}
		}

		private (List<JobHybridInput> samples, Dictionary<ObjectId, uint> studentIdToKey) PrepareJobRecommendationData(MongoDbContext dbContext)
		{
			var students = dbContext.GetCollection<Student>("students").Find(_ => true).ToList();
			var jobs = dbContext.GetCollection<Job>("jobs").Find(_ => true).ToList();
			var companies = dbContext.GetCollection<Company>("companies").Find(_ => true).ToList();
			var applications = dbContext.GetCollection<Application>("applications").Find(_ => true).ToList();

			var studentIdToKey = students.Select((s, i) => new { s.StudentId, Key = (uint)(i + 1) }).ToDictionary(x => x.StudentId, x => x.Key);
			var jobIdToKey = jobs.Select((j, i) => new { j.JobId, Key = (uint)(i + 1) }).ToDictionary(x => x.JobId, x => x.Key);
			var studentDict = students.ToDictionary(s => s.StudentId);
			var jobDict = jobs.ToDictionary(j => j.JobId);
			var companyDict = companies.ToDictionary(c => c.CompanyId);

			var positiveStatuses = new[] { "hired", "ratedByCompany", "ratedByStudent", "finished", "rated" };
			var negativeStatuses = new[] { "expired" };

			var positiveSamples = applications
				.Where(a => positiveStatuses.Contains(a.Status) && studentIdToKey.ContainsKey(a.StudentId) && jobIdToKey.ContainsKey(a.JobId) && studentDict.ContainsKey(a.StudentId) && jobDict.ContainsKey(a.JobId))
				.Select(a =>
				{
					var stu = studentDict[a.StudentId];
					var job = jobDict[a.JobId];
					return new JobHybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = true,
						CompanyRating = GetCompanyRating(job.CompanyId, companyDict),
						HourlyPay = (float)Math.Min(job.HourlyPay / 20.0, 1.0),
						JobTypeExperience = Math.Min(GetJobTypeExperience(stu, job.Type), 5),
						TraitMatchScore = (float)GetAverageTraitScore(stu, job.RequiredTraits)
					};
				}).ToList();

			var negativeSamples = applications
				.Where(a => negativeStatuses.Contains(a.Status) && studentIdToKey.ContainsKey(a.StudentId) && jobIdToKey.ContainsKey(a.JobId) && studentDict.ContainsKey(a.StudentId) && jobDict.ContainsKey(a.JobId))
				.Select(a =>
				{
					var stu = studentDict[a.StudentId];
					var job = jobDict[a.JobId];
					return new JobHybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = false,
						CompanyRating = GetCompanyRating(job.CompanyId, companyDict),
						HourlyPay = (float)Math.Min(job.HourlyPay / 20.0, 1.0),
						JobTypeExperience = Math.Min(GetJobTypeExperience(stu, job.Type), 5),
						TraitMatchScore = (float)GetAverageTraitScore(stu, job.RequiredTraits)
					};
				}).ToList();

			return (positiveSamples.Concat(negativeSamples).ToList(), studentIdToKey);
		}

		private (List<HybridInput> samples, Dictionary<ObjectId, uint> studentIdToKey) PrepareStudentRecommendationData(MongoDbContext dbContext)
		{
			var students = dbContext.GetCollection<Student>("students").Find(_ => true).ToList();
			var jobs = dbContext.GetCollection<Job>("jobs").Find(_ => true).ToList();
			var applications = dbContext.GetCollection<Application>("applications").Find(_ => true).ToList();

			var studentIdToKey = students.Select((s, i) => new { s.StudentId, Key = (uint)(i + 1) }).ToDictionary(x => x.StudentId, x => x.Key);
			var jobIdToKey = jobs.Select((j, i) => new { j.JobId, Key = (uint)(i + 1) }).ToDictionary(x => x.JobId, x => x.Key);

			var positiveStatuses = new[] { "hired", "ratedByCompany", "ratedByStudent", "finished", "rated" };
			var negativeStatuses = new[] { "expired" };

			var positiveSamples = applications
				.Where(a => positiveStatuses.Contains(a.Status) && studentIdToKey.ContainsKey(a.StudentId) && jobIdToKey.ContainsKey(a.JobId))
				.Select(a =>
				{
					var stu = students.First(s => s.StudentId == a.StudentId);
					var job = jobs.First(j => j.JobId == a.JobId);
					return new HybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = true,
						JobTypeExperience = GetJobTypeExperience(stu, job.Type),
						TraitScore = (float)GetTraitScore(stu, job.RequiredTraits),
						StudentRating = GetStudentRating(stu)
					};
				}).ToList();

			var negativeSamples = applications
				.Where(a => negativeStatuses.Contains(a.Status) && studentIdToKey.ContainsKey(a.StudentId) && jobIdToKey.ContainsKey(a.JobId))
				.Select(a =>
				{
					var stu = students.First(s => s.StudentId == a.StudentId);
					var job = jobs.First(j => j.JobId == a.JobId);
					return new HybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = false,
						JobTypeExperience = GetJobTypeExperience(stu, job.Type),
						TraitScore = (float)GetTraitScore(stu, job.RequiredTraits),
						StudentRating = GetStudentRating(stu)
					};
				}).ToList();

			return (positiveSamples.Concat(negativeSamples).ToList(), studentIdToKey);
		}

		private static float GetCompanyRating(ObjectId companyId, Dictionary<ObjectId, Company> companyDict)
		{
			if (!companyDict.TryGetValue(companyId, out var company) || company.ThumbsReceived == null)
				return 0.5f;
			var up = company.ThumbsReceived.Up;
			var down = company.ThumbsReceived.Down;
			return (up + down) == 0 ? 0.5f : (float)up / (up + down);
		}

		private static int GetJobTypeExperience(Student student, string type) =>
			student.JobTypesHistory?.FirstOrDefault(j => j.Type == type)?.Count ?? 0;

		private static double GetAverageTraitScore(Student student, List<string>? requiredTraits)
		{
			if (requiredTraits == null || requiredTraits.Count == 0) return 0;
			return requiredTraits.Select(trait =>
			{
				var t = student.Traits?.FirstOrDefault(tr => tr.Name == trait);
				if (t == null) return 0;
				var pos = t.Positive ?? 0;
				var neg = t.Negative ?? 0;
				return (pos + neg) == 0 ? 0 : (double)pos / (pos + neg);
			}).DefaultIfEmpty(0).Average();
		}

		private static double GetTraitScore(Student student, List<string>? requiredTraits)
		{
			if (requiredTraits == null || requiredTraits.Count == 0) return 0;
			return requiredTraits.Select(trait =>
			{
				var t = student.Traits?.FirstOrDefault(tr => tr.Name == trait);
				var pos = t?.Positive ?? 0;
				var neg = t?.Negative ?? 0;
				return (pos + neg) == 0 ? 0 : (double)pos / (pos + neg);
			}).DefaultIfEmpty(0).Average();
		}

		private static float GetStudentRating(Student student)
		{
			var up = student.ThumbsReceived?.Up ?? 0;
			var down = student.ThumbsReceived?.Down ?? 0;
			return (up + down) == 0 ? 0.2f : (float)up / (up + down);
		}
	}

	public class TrainingStatus
	{
		public bool IsTraining { get; set; }
		public bool IsEnabled { get; set; }
		public TimeSpan ScheduledTime { get; set; }
		public DateTime NextRunTime { get; set; }
	}
}
