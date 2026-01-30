using Microsoft.ML;
using MongoDB.Driver;
using StudentService.Domain.Services;
using StudentService.Domain.Services.Models;
using StudentService.Domain.ValueObjects;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;
using StudentService.Infrastructure.Services.Models;

namespace StudentService.Infrastructure.Services
{
	public class JobPayPredictionService : IJobPayPredictionService
	{
		private readonly MongoDbContext _context;

		public JobPayPredictionService(MongoDbContext context)
		{
			_context = context;
		}

		public Task<JobPayPredictionResult> EvaluateAsync(string algorithm)
		{
			var (trainView, testView, mlContext) = PrepareData();
			var pipeline = GetPipeline(mlContext, algorithm);
			var model = pipeline.Fit(trainView);
			var predictions = model.Transform(testView);

			var metrics = mlContext.Regression.Evaluate(predictions, nameof(JobPayData.HourlyPay), "Score");

			var actual = mlContext.Data
				.CreateEnumerable<JobPayData>(testView, reuseRowObject: false)
				.Select(j => j.HourlyPay)
				.ToArray();

			var predicted = mlContext.Data
				.CreateEnumerable<JobPayPrediction>(predictions, reuseRowObject: false)
				.Select(p => p.HourlyPay)
				.ToArray();

			return Task.FromResult(new JobPayPredictionResult
			{
				MAE = (float)metrics.MeanAbsoluteError,
				Predictions = predicted,
				Actuals = actual
			});
		}

		public Task<float> PredictAsync(string algorithm, JobPayPredictionInput input)
		{
			var (trainView, _, mlContext) = PrepareData();
			var pipeline = GetPipeline(mlContext, algorithm);
			var model = pipeline.Fit(trainView);

			var jobPayData = new JobPayData
			{
				Type = input.Type ?? string.Empty,
				PlaceOfWork = input.PlaceOfWork ?? string.Empty,
				RequiredTraits = input.RequiredTraits != null ? string.Join(",", input.RequiredTraits) : string.Empty
			};

			var predEngine = mlContext.Model.CreatePredictionEngine<JobPayData, JobPayPrediction>(model);
			var prediction = predEngine.Predict(jobPayData);

			return Task.FromResult(prediction.HourlyPay);
		}

		public Task<AllJobPayPredictionMetricsResult> EvaluateAllAlgorithmsAsync()
		{
			var (trainView, testView, mlContext) = PrepareData();
			var algorithms = new[] { "linear", "tree", "lbfgs" };

			var results = algorithms.Select(name =>
			{
				var pipeline = GetPipeline(mlContext, name);
				var model = pipeline.Fit(trainView);
				var predictions = model.Transform(testView);
				var metrics = mlContext.Regression.Evaluate(predictions, nameof(JobPayData.HourlyPay), "Score");

				return new JobPayAlgorithmMetrics
				{
					Algorithm = name,
					MAE = (float)metrics.MeanAbsoluteError,
					MSE = (float)metrics.MeanSquaredError,
					RMSE = (float)metrics.RootMeanSquaredError,
					RSquared = (float)metrics.RSquared
				};
			}).ToList();

			return Task.FromResult(new AllJobPayPredictionMetricsResult { Metrics = results });
		}

		private IEnumerable<JobPayData> GetJobPayData()
		{
			var jobs = _context.GetCollection<Job>("jobs")
				.Find(job => job.HourlyPay <= 10f)
				.ToList();

			return jobs.Select(job => new JobPayData
			{
				Type = job.Type ?? string.Empty,
				PlaceOfWork = job.PlaceOfWork ?? string.Empty,
				RequiredTraits = job.RequiredTraits != null ? string.Join(",", job.RequiredTraits) : string.Empty,
				HourlyPay = (float)job.HourlyPay
			});
		}

		private (IDataView TrainSet, IDataView TestSet, MLContext MlContext) PrepareData()
		{
			var mlContext = new MLContext(seed: 1);
			var data = GetJobPayData().ToList();
			var dataView = mlContext.Data.LoadFromEnumerable(data);
			var split = mlContext.Data.TrainTestSplit(dataView, testFraction: 0.3);
			return (split.TrainSet, split.TestSet, mlContext);
		}

		private static IEstimator<ITransformer> GetPipeline(MLContext mlContext, string algorithm)
		{
			var pipeline = mlContext.Transforms.Categorical.OneHotEncoding("TypeEncoded", nameof(JobPayData.Type))
				.Append(mlContext.Transforms.Categorical.OneHotEncoding("PlaceOfWorkEncoded", nameof(JobPayData.PlaceOfWork)))
				.Append(mlContext.Transforms.Text.FeaturizeText("TraitsFeatures", nameof(JobPayData.RequiredTraits)))
				.Append(mlContext.Transforms.Concatenate("Features", "TypeEncoded", "PlaceOfWorkEncoded", "TraitsFeatures"));

			return algorithm.ToLowerInvariant() switch
			{
				"linear" => pipeline.Append(mlContext.Regression.Trainers.Sdca(nameof(JobPayData.HourlyPay), "Features", maximumNumberOfIterations: 100)),
				"tree" => pipeline.Append(mlContext.Regression.Trainers.FastTree(nameof(JobPayData.HourlyPay), "Features")),
				"lbfgs" => pipeline.Append(mlContext.Regression.Trainers.LbfgsPoissonRegression(nameof(JobPayData.HourlyPay), "Features")),
				_ => throw new ArgumentException($"Unknown algorithm '{algorithm}'", nameof(algorithm))
			};
		}
	}
}
