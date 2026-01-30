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
	public class JobRecommendationService : IJobRecommendationService
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;
		private readonly ModelTrainingService? _modelTrainingService;

		public JobRecommendationService(MongoDbContext context, IMapper mapper, ModelTrainingService? modelTrainingService = null)
		{
			_context = context;
			_mapper = mapper;
			_modelTrainingService = modelTrainingService;
		}

		public async Task<IReadOnlyList<JobRecommendationResult>> RecommendJobsForStudentAsync(string studentId)
		{
			var studentObjId = ParseObjectId(studentId, nameof(studentId));

			var student = await _context.GetCollection<Student>("students")
				.Find(s => s.StudentId == studentObjId)
				.FirstOrDefaultAsync();

			if (student == null)
				return new List<JobRecommendationResult>();

			var allJobs = await _context.GetCollection<Job>("jobs").Find(_ => true).ToListAsync();
			var allCompanies = await _context.GetCollection<Company>("companies").Find(_ => true).ToListAsync();

			var today = DateTime.Today;
			var availableJobs = allJobs.Where(j => j.StartDate > today).ToList();

			return availableJobs
				.Select(job => new { Job = job, Score = CalculateJobScore(job, student, allCompanies) })
				.OrderByDescending(x => x.Score)
				.Select(x => new JobRecommendationResult
				{
					Job = _mapper.Map<JobBasicInfo>(x.Job),
					Score = (float)x.Score,
					Explanations = new List<RecommendationExplanation>()
				})
				.ToList();
		}

		public async Task<IReadOnlyList<JobRecommendationResult>> MLRecommendJobsForStudentAsync(string studentId)
		{
			var studentObjId = ParseObjectId(studentId, nameof(studentId));

			var students = await _context.GetCollection<Student>("students").Find(_ => true).ToListAsync();
			var jobs = await _context.GetCollection<Job>("jobs").Find(_ => true).ToListAsync();
			var companies = await _context.GetCollection<Company>("companies").Find(_ => true).ToListAsync();
			var applications = await _context.GetCollection<Application>("applications").Find(_ => true).ToListAsync();

			var student = students.FirstOrDefault(s => s.StudentId == studentObjId);
			if (student == null)
				return new List<JobRecommendationResult>();

			var studentIdToKey = students.Select((s, i) => new { s.StudentId, Key = (uint)(i + 1) }).ToDictionary(x => x.StudentId, x => x.Key);
			var jobIdToKey = jobs.Select((j, i) => new { j.JobId, Key = (uint)(i + 1) }).ToDictionary(x => x.JobId, x => x.Key);
			var companyDict = companies.ToDictionary(c => c.CompanyId);

			var (positiveSamples, negativeSamples) = BuildJobTrainingSamples(applications, students, jobs, companies, studentIdToKey, jobIdToKey);

			if (positiveSamples.Count == 0)
				return new List<JobRecommendationResult>();

			var allSamples = positiveSamples.Concat(negativeSamples).ToList();

			var mlContext = new MLContext(seed: 0);
			
			ITransformer? model = null;
			if (_modelTrainingService != null)
			{
				model = await _modelTrainingService.LoadProductionModelAsync(ModelType.JobRecommendation, mlContext);
			}

			if (model == null)
			{
				model = TrainJobModel(mlContext, allSamples);
			}

			var learnedWeights = LearnJobFeatureWeights(mlContext, allSamples);

			if (!studentIdToKey.TryGetValue(studentObjId, out uint requestedStudentKey))
				return new List<JobRecommendationResult>();

			var appliedJobIds = applications.Where(a => a.StudentId == studentObjId).Select(a => a.JobId).ToHashSet();
			var today = DateTime.Today;
			var availableJobs = jobs.Where(j => !appliedJobIds.Contains(j.JobId) && j.StartDate > today).ToList();

			if (availableJobs.Count == 0)
				return new List<JobRecommendationResult>();

			var topJobsWithScores = PredictAndRankJobsWithScores(mlContext, model, availableJobs, student, companies, jobIdToKey, requestedStudentKey);

			return topJobsWithScores
				.Select(x => CreateJobRecommendationResult(x.Job, x.Score, x.Features, companyDict, learnedWeights))
				.ToList();
		}

		private static JobFeatureWeights LearnJobFeatureWeights(MLContext mlContext, List<JobHybridInput> samples)
		{
			if (samples.Count < 10)
				return JobFeatureWeights.Default;

			try
			{
				var trainData = mlContext.Data.LoadFromEnumerable(samples);

				var pipeline = mlContext.Transforms.Concatenate("Features",
						nameof(JobHybridInput.CompanyRating),
						nameof(JobHybridInput.HourlyPay),
						nameof(JobHybridInput.JobTypeExperience),
						nameof(JobHybridInput.TraitMatchScore))
					.Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
						labelColumnName: nameof(JobHybridInput.Label),
						featureColumnName: "Features",
						maximumNumberOfIterations: 100));

				var linearModel = pipeline.Fit(trainData);

				var logisticRegressionModel = linearModel.LastTransformer.Model;
				var weights = logisticRegressionModel.SubModel.Weights.ToArray();

				if (weights.Length >= 4)
				{
					var learnedWeights = new JobFeatureWeights
					{
						CompanyRatingWeight = Math.Abs(weights[0]),
						HourlyPayWeight = Math.Abs(weights[1]),
						JobTypeExperienceWeight = Math.Abs(weights[2]),
						TraitMatchWeight = Math.Abs(weights[3])
					};
					learnedWeights.Normalize();
					return learnedWeights;
				}
			}
			catch
			{
			}

			return JobFeatureWeights.Default;
		}

		private static double CalculateJobScore(Job job, Student student, List<Company> companies)
		{
			const double companyWeight = 0.4;
			const double payWeight = 0.3;
			const double typeWeight = 0.2;
			const double traitWeight = 0.1;

			double companyScore = GetCompanyRating(job.CompanyId, companies);
			double payScore = Math.Min(job.HourlyPay / 20.0, 1.0);
			double typeScore = GetJobTypeExperience(student, job.Type) > 0 ? 1 : 0;
			double traitScore = GetAverageTraitScore(student, job.RequiredTraits);

			return companyScore * companyWeight + payScore * payWeight + typeScore * typeWeight + traitScore * traitWeight;
		}

		private static double GetCompanyRating(ObjectId companyId, List<Company> companies)
		{
			var company = companies.FirstOrDefault(c => c.CompanyId == companyId);
			if (company?.ThumbsReceived == null)
				return 0;

			var up = company.ThumbsReceived.Up;
			var down = company.ThumbsReceived.Down;
			return (up + down) == 0 ? 0 : (double)up / (up + down);
		}

		private static float GetCompanyRatingForML(ObjectId companyId, Dictionary<ObjectId, Company> companyDict)
		{
			if (!companyDict.TryGetValue(companyId, out var company) || company.ThumbsReceived == null)
				return 0.5f;

			var up = company.ThumbsReceived.Up;
			var down = company.ThumbsReceived.Down;
			return (up + down) == 0 ? 0.5f : (float)up / (up + down);
		}

		private static int GetJobTypeExperience(Student student, string type)
		{
			return student.JobTypesHistory?.FirstOrDefault(j => j.Type == type)?.Count ?? 0;
		}

		private static double GetAverageTraitScore(Student student, List<string>? requiredTraits)
		{
			if (requiredTraits == null || requiredTraits.Count == 0)
				return 0;

			return requiredTraits
				.Select(trait => GetTraitScore(student, trait))
				.DefaultIfEmpty(0)
				.Average();
		}

		private static double GetTraitScore(Student student, string trait)
		{
			var t = student.Traits?.FirstOrDefault(tr => tr.Name == trait);
			if (t == null)
				return 0;

			var pos = t.Positive ?? 0;
			var neg = t.Negative ?? 0;
			return (pos + neg) == 0 ? 0 : (double)pos / (pos + neg);
		}

		private static (List<JobHybridInput> positive, List<JobHybridInput> negative) BuildJobTrainingSamples(
			List<Application> applications,
			List<Student> students,
			List<Job> jobs,
			List<Company> companies,
			Dictionary<ObjectId, uint> studentIdToKey,
			Dictionary<ObjectId, uint> jobIdToKey)
		{
			var studentDict = students.ToDictionary(s => s.StudentId);
			var jobDict = jobs.ToDictionary(j => j.JobId);
			var companyDict = companies.ToDictionary(c => c.CompanyId);

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
					return new JobHybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = true,
						CompanyRating = GetCompanyRatingForML(job.CompanyId, companyDict),
						HourlyPay = (float)Math.Min(job.HourlyPay / 20.0, 1.0),
						JobTypeExperience = Math.Min(GetJobTypeExperience(stu, job.Type), 5),
						TraitMatchScore = (float)GetAverageTraitScore(stu, job.RequiredTraits)
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
					return new JobHybridInput
					{
						StudentKey = studentIdToKey[a.StudentId],
						JobKey = jobIdToKey[a.JobId],
						Label = false,
						CompanyRating = GetCompanyRatingForML(job.CompanyId, companyDict),
						HourlyPay = (float)Math.Min(job.HourlyPay / 20.0, 1.0),
						JobTypeExperience = Math.Min(GetJobTypeExperience(stu, job.Type), 5),
						TraitMatchScore = (float)GetAverageTraitScore(stu, job.RequiredTraits)
					};
				})
				.ToList();

			return (positiveSamples, negativeSamples);
		}

		private static ITransformer TrainJobModel(MLContext mlContext, List<JobHybridInput> samples)
		{
			var trainData = mlContext.Data.LoadFromEnumerable(samples);

			var pipeline = mlContext.Transforms.Conversion
				.MapValueToKey("studentKeyIndex", nameof(JobHybridInput.StudentKey))
				.Append(mlContext.Transforms.Categorical.OneHotEncoding("studentKeyVec", "studentKeyIndex"))
				.Append(mlContext.Transforms.Conversion.MapValueToKey("jobKeyIndex", nameof(JobHybridInput.JobKey)))
				.Append(mlContext.Transforms.Categorical.OneHotEncoding("jobKeyVec", "jobKeyIndex"))
				.Append(mlContext.Transforms.Concatenate("Features",
					"studentKeyVec", "jobKeyVec",
					nameof(JobHybridInput.CompanyRating),
					nameof(JobHybridInput.HourlyPay),
					nameof(JobHybridInput.JobTypeExperience),
					nameof(JobHybridInput.TraitMatchScore)))
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
						Shuffle = true,
						Radius = 0.01f,
						Verbose = false
					}));

			return pipeline.Fit(trainData);
		}

		private static List<(Job Job, float Score, JobFeatures Features)> PredictAndRankJobsWithScores(
			MLContext mlContext,
			ITransformer model,
			List<Job> availableJobs,
			Student student,
			List<Company> companies,
			Dictionary<ObjectId, uint> jobIdToKey,
			uint requestedStudentKey)
		{
			var companyDict = companies.ToDictionary(c => c.CompanyId);

			var batchInputs = availableJobs
				.Where(j => jobIdToKey.ContainsKey(j.JobId))
				.Select(j => new JobHybridInput
				{
					StudentKey = requestedStudentKey,
					JobKey = jobIdToKey[j.JobId],
					Label = false,
					CompanyRating = GetCompanyRatingForML(j.CompanyId, companyDict),
					HourlyPay = (float)Math.Min(j.HourlyPay / 20.0, 1.0),
					JobTypeExperience = Math.Min(GetJobTypeExperience(student, j.Type), 5),
					TraitMatchScore = (float)GetAverageTraitScore(student, j.RequiredTraits)
				})
				.ToList();

			var batchDv = mlContext.Data.LoadFromEnumerable(batchInputs);
			var predictions = model.Transform(batchDv);

			var scoredResults = mlContext.Data
				.CreateEnumerable<FFMOutput>(predictions, reuseRowObject: false)
				.ToList();

			var validJobs = availableJobs
				.Where(j => jobIdToKey.ContainsKey(j.JobId))
				.ToList();

			return validJobs
				.Zip(batchInputs, (job, input) => new { Job = job, Input = input })
				.Zip(scoredResults, (x, pred) => (
					x.Job,
					pred.Score,
					new JobFeatures
					{
						CompanyRating = x.Input.CompanyRating,
						HourlyPay = x.Input.HourlyPay,
						JobTypeExperience = x.Input.JobTypeExperience,
						TraitMatchScore = x.Input.TraitMatchScore,
						ActualHourlyPay = x.Job.HourlyPay,
						JobType = x.Job.Type
					}
				))
				.OrderByDescending(x => x.Score)
				.ToList();
		}

		private JobRecommendationResult CreateJobRecommendationResult(
			Job job,
			float score,
			JobFeatures features,
			Dictionary<ObjectId, Company> companyDict,
			JobFeatureWeights learnedWeights)
		{
			var jobInfo = _mapper.Map<JobBasicInfo>(job);
			var explanations = GenerateJobExplanations(features, job, companyDict, learnedWeights);

			return new JobRecommendationResult
			{
				Job = jobInfo,
				Score = score,
				Explanations = explanations
			};
		}

		private static List<RecommendationExplanation> GenerateJobExplanations(
			JobFeatures features,
			Job job,
			Dictionary<ObjectId, Company> companyDict,
			JobFeatureWeights weights)
		{
			var explanations = new List<RecommendationExplanation>();

			var companyName = companyDict.TryGetValue(job.CompanyId, out var company) ? company.Name : "Nepoznato";
			var companyContribution = features.CompanyRating * weights.CompanyRatingWeight;
			if (features.CompanyRating >= 0.4f)
			{
				string companyDescription = features.CompanyRating switch
				{
					>= 0.8f => $"{companyName} ima izvrsnu reputaciju među studentima",
					>= 0.6f => $"{companyName} ima dobru reputaciju među studentima",
					_ => $"{companyName} ima prosječnu reputaciju"
				};
				explanations.Add(new RecommendationExplanation
				{
					FeatureName = "Ocjena tvrtke",
					Description = companyDescription,
					Value = features.CompanyRating,
					Contribution = companyContribution
				});
			}

			var payContribution = features.HourlyPay * weights.HourlyPayWeight;
			string payDescription = features.ActualHourlyPay switch
			{
				>= 15 => $"Visoka satnica ({features.ActualHourlyPay:F2} €/sat)",
				>= 10 => $"Dobra satnica ({features.ActualHourlyPay:F2} €/sat)",
				>= 7 => $"Prosječna satnica ({features.ActualHourlyPay:F2} €/sat)",
				_ => $"Satnica ispod prosjeka ({features.ActualHourlyPay:F2} €/sat)"
			};
			explanations.Add(new RecommendationExplanation
			{
				FeatureName = "Satnica",
				Description = payDescription,
				Value = features.HourlyPay,
				Contribution = payContribution
			});

			var expContribution = features.JobTypeExperience * weights.JobTypeExperienceWeight / 5f;
			string expDescription = features.JobTypeExperience switch
			{
				>= 3 => $"Imaš mnogo iskustva u poslovima tipa {features.JobType} ({(int)features.JobTypeExperience} prethodnih poslova)",
				>= 1 => $"Imaš iskustva u poslovima tipa {features.JobType} ({(int)features.JobTypeExperience} prethodni/prethodnih posao/poslova)",
				_ => $"Nova prilika za stjecanje iskustva u poslovima tipa {features.JobType}"
			};
			explanations.Add(new RecommendationExplanation
			{
				FeatureName = "Iskustvo s vrstom posla",
				Description = expDescription,
				Value = features.JobTypeExperience,
				Contribution = expContribution
			});

			var traitContribution = features.TraitMatchScore * weights.TraitMatchWeight;
			string traitDescription = features.TraitMatchScore switch
			{
				>= 0.8f => "Tvoje vještine odlično odgovaraju ovom poslu",
				>= 0.6f => "Tvoje vještine dobro odgovaraju ovom poslu",
				>= 0.4f => "Tvoje vještine djelomično odgovaraju zahtjevima posla",
				>= 0.2f => "Neke od tvojih vještina odgovaraju zahtjevima posla",
				_ => "Ovaj posao ima drugačije zahtjeve za vještine"
			};
			explanations.Add(new RecommendationExplanation
			{
				FeatureName = "Podudaranje vještina",
				Description = traitDescription,
				Value = features.TraitMatchScore,
				Contribution = traitContribution
			});

			return explanations.OrderByDescending(e => e.Contribution).ToList();
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}

	internal class JobFeatures
	{
		public float CompanyRating { get; set; }
		public float HourlyPay { get; set; }
		public float JobTypeExperience { get; set; }
		public float TraitMatchScore { get; set; }
		public double ActualHourlyPay { get; set; }
		public string JobType { get; set; } = string.Empty;
	}
}

