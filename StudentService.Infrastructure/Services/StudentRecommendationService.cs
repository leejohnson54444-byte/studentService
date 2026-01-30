using AutoMapper;
using Microsoft.ML;
using Microsoft.ML.Trainers;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Services;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.MLflow;
using StudentService.Infrastructure.Models;
using StudentService.Infrastructure.Services.Models;

namespace StudentService.Infrastructure.Services
{
	public class StudentRecommendationService : IStudentRecommendationService
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;
		private readonly ModelTrainingService? _modelTrainingService;

		public StudentRecommendationService(MongoDbContext context, IMapper mapper, ModelTrainingService? modelTrainingService = null)
		{
			_context = context;
			_mapper = mapper;
			_modelTrainingService = modelTrainingService;
		}

		public async Task<IReadOnlyList<ApplicationInfo>> RecommendStudentsForJobAsync(string jobId)
		{
			var jobObjId = ParseObjectId(jobId, nameof(jobId));

			var job = await _context.GetCollection<Job>("jobs")
				.Find(j => j.JobId == jobObjId)
				.FirstOrDefaultAsync();

			if (job == null)
				return Array.Empty<ApplicationInfo>();

			var applications = await _context.GetCollection<Application>("applications")
				.Find(a => a.JobId == jobObjId)
				.ToListAsync();

			var studentIds = applications.Select(a => a.StudentId).ToList();
			var students = await _context.GetCollection<Student>("students")
				.Find(s => studentIds.Contains(s.StudentId))
				.ToListAsync();

			var scoredStudents = students
				.Select(s => new { Student = s, Score = CalculateStudentScore(s, job) })
				.OrderByDescending(x => x.Score)
				.ToList();

			return scoredStudents
				.Select(sc => CreateApplicationInfo(sc.Student, applications))
				.ToList();
		}

		public async Task<IReadOnlyList<StudentRecommendationResult>> MLRecommendStudentsForJobAsync(string jobId)
		{
			var jobObjId = ParseObjectId(jobId, nameof(jobId));

			var students = await _context.GetCollection<Student>("students").Find(_ => true).ToListAsync();
			var jobs = await _context.GetCollection<Job>("jobs").Find(_ => true).ToListAsync();
			var companies = await _context.GetCollection<Company>("companies").Find(_ => true).ToListAsync();
			var applications = await _context.GetCollection<Application>("applications").Find(_ => true).ToListAsync();

			var studentIdToKey = students.Select((s, i) => new { s.StudentId, Key = (uint)(i + 1) }).ToDictionary(x => x.StudentId, x => x.Key);
			var jobIdToKey = jobs.Select((j, i) => new { j.JobId, Key = (uint)(i + 1) }).ToDictionary(x => x.JobId, x => x.Key);
			var companyDict = companies.ToDictionary(c => c.CompanyId);

			var (positiveSamples, negativeSamples) = BuildTrainingSamples(applications, students, jobs, companyDict, studentIdToKey, jobIdToKey);
			
			if (positiveSamples.Count == 0)
				return new List<StudentRecommendationResult>();

			var allSamples = positiveSamples.Concat(negativeSamples).ToList();

			var mlContext = new MLContext(seed: 0);
			
			ITransformer? model = null;
			if (_modelTrainingService != null)
			{
				model = await _modelTrainingService.LoadProductionModelAsync(ModelType.StudentRecommendation, mlContext);
			}

			if (model == null)
			{
				model = TrainModel(mlContext, allSamples);
			}

			var learnedWeights = LearnStudentFeatureWeights(mlContext, allSamples);

			if (!jobIdToKey.TryGetValue(jobObjId, out uint requestedJobKey))
				return new List<StudentRecommendationResult>();

			var jobForPrediction = jobs.First(j => j.JobId == jobObjId);
			var appliedApps = applications.Where(a => a.JobId == jobObjId).ToList();

			if (appliedApps.Count == 0)
				return new List<StudentRecommendationResult>();

			var appliedStudents = appliedApps
				.Select(a => students.First(s => s.StudentId == a.StudentId))
				.Distinct()
				.ToList();

			var topStudentsWithScores = PredictAndRankStudentsWithScores(mlContext, model, appliedStudents, studentIdToKey, requestedJobKey, jobForPrediction, companyDict);

			return topStudentsWithScores
				.Select(x => CreateStudentRecommendationResult(x.Student, x.Score, x.Features, appliedApps, jobForPrediction, learnedWeights))
				.ToList();
		}

		private static StudentFeatureWeights LearnStudentFeatureWeights(MLContext mlContext, List<HybridInput> samples)
		{
			if (samples.Count < 10)
				return StudentFeatureWeights.Default;

			try
			{
				var trainData = mlContext.Data.LoadFromEnumerable(samples);

				var pipeline = mlContext.Transforms.Concatenate("Features",
						nameof(HybridInput.JobTypeExperience),
						nameof(HybridInput.TraitScore),
						nameof(HybridInput.StudentRating))
					.Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
						labelColumnName: nameof(HybridInput.Label),
						featureColumnName: "Features",
						maximumNumberOfIterations: 100));

				var linearModel = pipeline.Fit(trainData);

				var logisticRegressionModel = linearModel.LastTransformer.Model;
				var weights = logisticRegressionModel.SubModel.Weights.ToArray();

				if (weights.Length >= 3)
				{
					var learnedWeights = new StudentFeatureWeights
					{
						ExperienceWeight = Math.Abs(weights[0]),
						TraitScoreWeight = Math.Abs(weights[1]),
						StudentRatingWeight = Math.Abs(weights[2])
					};
					learnedWeights.Normalize();
					return learnedWeights;
				}
			}
			catch
			{
			}

			return StudentFeatureWeights.Default;
		}

		private double CalculateStudentScore(Student student, Job job)
		{
			const double expWeight = 0.3;
			const double ratingWeight = 0.5;
			const double traitWeight = 0.2;

			double expScore = Math.Min(GetExperience(student, job.Type) / 5.0, 1.0);
			double ratingScore = GetRating(student);
			double traitScore = GetTraitScore(student, job.RequiredTraits);

			return expScore * expWeight + ratingScore * ratingWeight + traitScore * traitWeight;
		}

		private static int GetExperience(Student student, string jobType) =>
			student.JobTypesHistory?.FirstOrDefault(j => j.Type == jobType)?.Count ?? 0;

		private static double GetRating(Student student)
		{
			var up = student.ThumbsReceived?.Up ?? 0;
			var down = student.ThumbsReceived?.Down ?? 0;
			return (up + down) == 0 ? 0.9 : (double)up / (up + down);
		}

		private static double GetTraitScore(Student student, List<string>? requiredTraits)
		{
			if (requiredTraits == null || requiredTraits.Count == 0)
				return 0;

			return requiredTraits
				.Select(trait =>
				{
					var t = student.Traits?.FirstOrDefault(tr => tr.Name == trait);
					var pos = t?.Positive ?? 0;
					var neg = t?.Negative ?? 0;
					return (pos + neg) == 0 ? 0 : (double)pos / (pos + neg);
				})
				.DefaultIfEmpty(0)
				.Average();
		}

		private static float GetEmployerRatingForML(ObjectId companyId, Dictionary<ObjectId, Company> companyDict)
		{
			if (!companyDict.TryGetValue(companyId, out var company) || company.ThumbsReceived == null)
				return 0.5f;

			var up = company.ThumbsReceived.Up;
			var down = company.ThumbsReceived.Down;
			return (up + down) == 0 ? 0.5f : (float)up / (up + down);
		}

		private ApplicationInfo CreateApplicationInfo(Student student, List<Application> applications)
		{
			var latestApp = applications.First(a => a.StudentId == student.StudentId);
			var dto = _mapper.Map<ApplicationInfo>(latestApp);
			dto.StudentBasicInfo = _mapper.Map<StudentBasicInfo>(student);
			return dto;
		}

		private StudentRecommendationResult CreateStudentRecommendationResult(
			Student student,
			float score,
			StudentFeatures features,
			List<Application> applications,
			Job job,
			StudentFeatureWeights learnedWeights)
		{
			var latestApp = applications.First(a => a.StudentId == student.StudentId);
			var appInfo = _mapper.Map<ApplicationInfo>(latestApp);
			appInfo.StudentBasicInfo = _mapper.Map<StudentBasicInfo>(student);

			var explanations = GenerateStudentExplanations(features, job, learnedWeights);

			return new StudentRecommendationResult
			{
				Application = appInfo,
				Score = score,
				Explanations = explanations
			};
		}

		private static List<RecommendationExplanation> GenerateStudentExplanations(
			StudentFeatures features,
			Job job,
			StudentFeatureWeights weights)
		{
			var explanations = new List<RecommendationExplanation>();

			var expContribution = features.JobTypeExperience * weights.ExperienceWeight;
			string expDescription = features.JobTypeExperience switch
			{
				>= 3 => $"Mnogo iskustva u poslu tipa {job.Type}, ({features.JobTypeExperience} prošlih poslova)",
				>= 1 => $"Ima iskustva u poslu tipa {job.Type}, ({features.JobTypeExperience} prošli/prošlih posao/poslova)",
				_ => $"Nema iskustva u poslu tipa {job.Type}"
			};
			explanations.Add(new RecommendationExplanation
			{
				FeatureName = "Job Type Experience",
				Description = expDescription,
				Value = features.JobTypeExperience,
				Contribution = expContribution
			});

			var traitContribution = features.TraitScore * weights.TraitScoreWeight;
			string traitDescription = features.TraitScore switch
			{
				>= 0.8f => "Savršena podudarnost s traženim vještinama",
				>= 0.6f => "Odlična podudarnost s traženim vještinama",
				>= 0.4f => "Dobra podudarnost s traženim vještinama",
				>= 0.2f => "Postoji podudarnost s traženim vještinama",
				_ => "Slaba ili nepostojeća podudarnost s traženim vještinama"
            };
			explanations.Add(new RecommendationExplanation
			{
				FeatureName = "Trait Match",
				Description = traitDescription,
				Value = features.TraitScore,
				Contribution = traitContribution
			});

			var ratingContribution = features.StudentRating * weights.StudentRatingWeight;
			string ratingDescription = features.StudentRating switch
			{
				>= 0.9f => "Odlično ocijenjen/a od strane drugih poslodavaca",
				>= 0.7f => "Dobro ocijenjen/a od strane drugih poslodavaca",
				>= 0.5f => "Prosječno ocijenjen/a od strane drugih poslodavaca",
				_ => "Slaba ili nepostojeća povijest ocjena."
			};
			explanations.Add(new RecommendationExplanation
			{
				FeatureName = "Student Rating",
				Description = ratingDescription,
				Value = features.StudentRating,
				Contribution = ratingContribution
			});

			return explanations.OrderByDescending(e => e.Contribution).ToList();
		}

		private static (List<HybridInput> positive, List<HybridInput> negative) BuildTrainingSamples(
			List<Application> applications,
			List<Student> students,
			List<Job> jobs,
			Dictionary<ObjectId, Company> companyDict,
			Dictionary<ObjectId, uint> studentIdToKey,
			Dictionary<ObjectId, uint> jobIdToKey)
		{
			var studentDict = students.ToDictionary(s => s.StudentId);
			var jobDict = jobs.ToDictionary(j => j.JobId);

			var positiveStatuses = new[] { "hired", "ratedByCompany", "ratedByStudent", "finished", "rated" };
			var negativeStatuses = new[] { "expired" };

			var positiveSamples = applications
				.Where(a => positiveStatuses.Contains(a.Status)
					&& studentIdToKey.ContainsKey(a.StudentId)
					&& jobIdToKey.ContainsKey(a.JobId)
					&& studentDict.ContainsKey(a.StudentId)
					&& jobDict.ContainsKey(a.JobId))
				.Select(a =>
				{
					var stu = studentDict[a.StudentId];
					var job = jobDict[a.JobId];
					return new HybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = true,
						JobTypeExperience = GetExperience(stu, job.Type),
						TraitScore = (float)GetTraitScore(stu, job.RequiredTraits),
						StudentRating = GetStudentRatingForML(stu),
						EmployerRating = GetEmployerRatingForML(job.CompanyId, companyDict),
						HourlyPay = (float)Math.Min(job.HourlyPay / 20.0, 1.0)
					};
				})
				.ToList();

			var negativeSamples = applications
				.Where(a => negativeStatuses.Contains(a.Status)
					&& studentIdToKey.ContainsKey(a.StudentId)
					&& jobIdToKey.ContainsKey(a.JobId)
					&& studentDict.ContainsKey(a.StudentId)
					&& jobDict.ContainsKey(a.JobId))
				.Select(a =>
				{
					var stu = studentDict[a.StudentId];
					var job = jobDict[a.JobId];
					return new HybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = false,
						JobTypeExperience = GetExperience(stu, job.Type),
						TraitScore = (float)GetTraitScore(stu, job.RequiredTraits),
						StudentRating = GetStudentRatingForML(stu),
						EmployerRating = GetEmployerRatingForML(job.CompanyId, companyDict),
						HourlyPay = (float)Math.Min(job.HourlyPay / 20.0, 1.0)
					};
				})
				.ToList();

			return (positiveSamples, negativeSamples);
		}

		private static float GetStudentRatingForML(Student student)
		{
			var up = student.ThumbsReceived?.Up ?? 0;
			var down = student.ThumbsReceived?.Down ?? 0;
			return (up + down) == 0 ? 0.2f : (float)up / (up + down);
		}

		private static ITransformer TrainModel(MLContext mlContext, List<HybridInput> samples)
		{
			var trainData = mlContext.Data.LoadFromEnumerable(samples);

			var pipeline = mlContext.Transforms.Conversion
				.MapValueToKey("studentKeyIndex", nameof(HybridInput.StudentKey))
				.Append(mlContext.Transforms.Categorical.OneHotEncoding("studentKeyVec", "studentKeyIndex"))
				.Append(mlContext.Transforms.Conversion.MapValueToKey("jobKeyIndex", nameof(HybridInput.JobKey)))
				.Append(mlContext.Transforms.Categorical.OneHotEncoding("jobKeyVec", "jobKeyIndex"))
				.Append(mlContext.Transforms.Concatenate("Features",
					"studentKeyVec", "jobKeyVec",
					nameof(HybridInput.JobTypeExperience),
					nameof(HybridInput.TraitScore),
					nameof(HybridInput.StudentRating),
					nameof(HybridInput.EmployerRating),
					nameof(HybridInput.HourlyPay)))
				.Append(mlContext.BinaryClassification.Trainers.FieldAwareFactorizationMachine(
					new FieldAwareFactorizationMachineTrainer.Options
					{
						FeatureColumnName = "Features",
						LabelColumnName = nameof(HybridInput.Label),
						LatentDimension = 16,
						NumberOfIterations = 50,
						LearningRate = 0.2f,
						LambdaLinear = 0.0001f,
						LambdaLatent = 0.0001f,
						NormalizeFeatures = true,
						Shuffle = true,
						Radius = 0.01f,
						Verbose = false
					}));

			return pipeline.Fit(trainData);
		}

		private static List<(Student Student, float Score, StudentFeatures Features)> PredictAndRankStudentsWithScores(
			MLContext mlContext,
			ITransformer model,
			List<Student> appliedStudents,
			Dictionary<ObjectId, uint> studentIdToKey,
			uint requestedJobKey,
			Job jobForPrediction,
			Dictionary<ObjectId, Company> companyDict)
		{
			var batchInputs = appliedStudents
				.Where(s => studentIdToKey.ContainsKey(s.StudentId))
				.Select(s => new HybridInput
				{
					StudentKey = studentIdToKey[s.StudentId],
					JobKey = requestedJobKey,
					Label = false,
					JobTypeExperience = GetExperience(s, jobForPrediction.Type),
					TraitScore = (float)GetTraitScore(s, jobForPrediction.RequiredTraits),
					StudentRating = GetStudentRatingForML(s),
					EmployerRating = GetEmployerRatingForML(jobForPrediction.CompanyId, companyDict),
					HourlyPay = (float)Math.Min(jobForPrediction.HourlyPay / 20.0, 1.0)
				})
				.ToList();

			var batchDv = mlContext.Data.LoadFromEnumerable(batchInputs);
			var predictions = model.Transform(batchDv);

			var scoredResults = mlContext.Data
				.CreateEnumerable<FFMOutput>(predictions, reuseRowObject: false)
				.ToList();

			var validStudents = appliedStudents
				.Where(s => studentIdToKey.ContainsKey(s.StudentId))
				.ToList();

			return validStudents
				.Zip(batchInputs, (stu, input) => new { Student = stu, Input = input })
				.Zip(scoredResults, (x, pred) => (
					x.Student,
					pred.Score,
					new StudentFeatures
					{
						JobTypeExperience = x.Input.JobTypeExperience,
						TraitScore = x.Input.TraitScore,
						StudentRating = x.Input.StudentRating
					}
				))
				.OrderByDescending(x => x.Score)
				.ToList();
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}

	internal class StudentFeatures
	{
		public float JobTypeExperience { get; set; }
		public float TraitScore { get; set; }
		public float StudentRating { get; set; }
	}
}
