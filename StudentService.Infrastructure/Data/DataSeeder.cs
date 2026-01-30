using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.Services;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Data
{
	public class DataSeeder
	{
		private readonly IMongoCollection<Job> _jobs;
		private readonly IMongoCollection<Student> _students;
		private readonly IMongoCollection<Company> _companies;
		private readonly IMongoCollection<Application> _applications;
		private readonly DataScraper _scraper;
		private readonly IStudentRecommendationService _recommender;
		private readonly Random _random = new();

		private static readonly List<string> FirstNames = new()
		{
			"Marko", "Ivan", "Josip", "Luka", "Matej", "Petar", "Antonio", "Filip", "Karlo", "Dino",
			"Ana", "Iva", "Marija", "Petra", "Lucija", "Sara", "Nina", "Ivana", "Maja", "Karla",
			"Stjepan", "Domagoj", "Tomislav", "Hrvoje", "Matija", "Dominik", "Dario", "Nikola", "Lovro", "Bruno",
			"Marta", "Lea", "Elena", "Laura", "Lana", "Ema", "Tena", "Mia", "Dora", "Klara", "Franko", "Zvonimir",
			"Kristijan", "Branimir", "Silvije", "Vedran", "Sandro", "Dalibor",
			"Tea", "Vesna", "Dragana", "Zrinka", "Snježana", "Gordana", "Željka", "Tina"
		};

		private static readonly List<string> LastNames = new()
		{
			"Horvat", "Kovačić", "Novak", "Božić", "Marić", "Jurić", "Pavić", "Kovač", "Knežević", "Vuković",
			"Matić", "Petrović", "Tomić", "Blažević", "Babić", "Perić", "Filipović", "Mandić", "Lovrić", "Grgić",
			"Vučković", "Šimić", "Jukić", "Marković", "Barišić", "Pavlović", "Lončar", "Vlahović", "Radić", "Jovanović",
			"Martinović", "Nikolić", "Bošnjak", "Vidović", "Brkić", "Topić", "Galić", "Matijević", "Ivić",
			"Ćorić", "Šarić", "Vidaković", "Prpić", "Čović", "Golubić", "Lovreković", "Kolar", "Sertić", "Špoljar",
			"Župan", "Ostojić", "Burić", "Živković", "Leko", "Pejić", "Ćurčić", "Radošević", "Fabijanić", "Ćosić"
		};

		private static readonly Dictionary<string, double> JobTypeDistribution = new()
		{
			["Administrativni poslovi"] = 0.05,
			["Anketiranje / Služba za korisnike"] = 0.025,
			["Ekonomija, računovodstvo i financije"] = 0.015,
			["Fizički poslovi"] = 0.05,
			["IT poslovi, elektrotehnika i strojarstvo"] = 0.005,
			["Promotivne aktivnosti"] = 0.055,
			["Rad s djecom/animator"] = 0.01,
			["Rad u skladištu/proizvodnji"] = 0.055,
			["Razno"] = 0.175,
			["Studentske prakse"] = 0.002,
			["Transport i dostava"] = 0.025,
			["Trgovina"] = 0.273,
			["Turizam i ugostiteljstvo"] = 0.25,
			["Zdravstvo"] = 0.005,
			["Znanost i obrazovanje"] = 0.005,
		};

		public DataSeeder(MongoDbContext context, DataScraper scraper, IStudentRecommendationService recommender)
		{
			_jobs = context.GetCollection<Job>("jobs");
			_students = context.GetCollection<Student>("students");
			_companies = context.GetCollection<Company>("companies");
			_applications = context.GetCollection<Application>("applications");
			_scraper = scraper;
			_recommender = recommender;
		}

		public async Task OneTimeSeedDataAsync()
		{
			await ScrapeNewJobs();
			await UpdateJobDatesToHistoricalAsync();
			await CreateStudentsAsync(10000, false);
			await CreateCompanyRatingsAsync();
			await CreateApplicationsAsync(true);
			await UpdateApplicationsAsync(true);
		}

		public async Task DailySeedDataAsync()
		{
			await ScrapeNewJobs();
			await CreateStudentsAsync(75, true);
			await CreateCompanyRatingsAsync();
			await CreateApplicationsAsync(false);
			await UpdateApplicationsAsync(false);
		}

		private async Task UpdateJobDatesToHistoricalAsync()
		{
			var allJobs = await _jobs.Find(_ => true).ToListAsync();
			var jobsToUpdate = (int)(allJobs.Count * 0.3);
			var jobsToMakeHistorical = allJobs.OrderBy(_ => _random.Next()).Take(jobsToUpdate).ToList();

			foreach (var job in jobsToMakeHistorical)
			{
				int monthsBack = _random.Next(2, 5);
				job.StartDate = job.StartDate.AddMonths(-monthsBack);
				job.EndDate = job.EndDate.AddMonths(-monthsBack);
				await _jobs.ReplaceOneAsync(j => j.JobId == job.JobId, job);
			}
		}

		private async Task CreateStudentsAsync(int totalStudents, bool isNew)
		{
			var students = new List<Student>(totalStudents);

			if (isNew)
			{
				for (int i = 0; i < totalStudents; i++)
				{
					students.Add(CreateNewStudent());
				}
			}
			else
			{
				var jobsByType = (await _jobs.Find(_ => true).ToListAsync())
					.GroupBy(j => j.Type)
					.ToDictionary(g => g.Key, g => g.ToList());

				for (int i = 0; i < totalStudents; i++)
				{
					students.Add(CreateExperiencedStudent(jobsByType));
				}
			}

			await _students.InsertManyAsync(students);
		}

		private Student CreateNewStudent() => new()
		{
			StudentId = ObjectId.GenerateNewId(),
			FirstName = FirstNames[_random.Next(FirstNames.Count)],
			LastName = LastNames[_random.Next(LastNames.Count)],
			JobTypesHistory = null,
			ThumbsReceived = null,
			Traits = null
		};

		private Student CreateExperiencedStudent(Dictionary<string, List<Job>> jobsByType)
		{
			int expCount = GetExperienceCount();

			if (expCount == 0)
				return CreateNewStudent();

			double rating = GetRandomRating();
			var jtHistory = BuildJobTypeHistory(expCount);
			int ratedCount = (int)Math.Round(expCount * (_random.NextDouble() * 0.2 + 0.4));
			int upCount = (int)Math.Round((rating - 1.0) / 4.0 * ratedCount);
			int downCount = ratedCount - upCount;

			var student = new Student
			{
				StudentId = ObjectId.GenerateNewId(),
				FirstName = FirstNames[_random.Next(FirstNames.Count)],
				LastName = LastNames[_random.Next(LastNames.Count)],
				JobTypesHistory = jtHistory,
				ThumbsReceived = new ThumbsReceived { Up = upCount, Down = downCount },
				Traits = new List<Trait>()
			};

			var expIndices = Enumerable.Range(0, expCount).OrderBy(_ => _random.Next()).Take(ratedCount).ToList();

			foreach (var idx in expIndices.Take(upCount))
				ApplyTraits(student, jtHistory[GetHistoryIndex(jtHistory, idx)].Type, true, jobsByType);

			foreach (var idx in expIndices.Skip(upCount))
				ApplyTraits(student, jtHistory[GetHistoryIndex(jtHistory, idx)].Type, false, jobsByType);

			if (student.Traits.Count == 0)
				student.Traits = null;

			return student;
		}

		private int GetExperienceCount()
		{
			double randomValue = _random.NextDouble();
			return randomValue switch
			{
				<= 0.25 => 1,
				<= 0.40 => 2,
				<= 0.50 => 3,
				<= 0.55 => 4,
				<= 0.58 => 5,
				<= 0.59 => _random.Next(6, 11),
				<= 0.595 => _random.Next(11, 21),
				<= 0.598 => _random.Next(21, 51),
				<= 0.60 => _random.Next(51, 101),
				_ => 0
			};
		}

		private double GetRandomRating()
		{
			double randomValue = _random.NextDouble();
			return randomValue switch
			{
				< 0.10 => Math.Round(_random.NextDouble() * 1.5 + 1.0, 2),
				< 0.30 => Math.Round(_random.NextDouble() * 0.99 + 2.51, 2),
				< 0.75 => Math.Round(_random.NextDouble() * 0.99 + 3.51, 2),
				_ => Math.Round(_random.NextDouble() * 0.49 + 4.51, 2)
			};
		}

		private List<JobTypeHistory> BuildJobTypeHistory(int expCount)
		{
			var jtHistory = new List<JobTypeHistory>();
			var chosenTypes = new List<string>();

			string firstType = PickJobType(null);
			chosenTypes.Add(firstType);
			jtHistory.Add(new JobTypeHistory { Type = firstType, Count = 1 });

			for (int e = 2; e <= expCount; e++)
			{
				double chooseDiffProb = chosenTypes.Count < 2
					? _random.NextDouble() * 0.1 + 0.15
					: _random.NextDouble() * 0.05 + 0.05;

				if (_random.NextDouble() < chooseDiffProb)
				{
					var nextType = PickJobType(new HashSet<string>(chosenTypes));
					chosenTypes.Add(nextType);
					jtHistory.Add(new JobTypeHistory { Type = nextType, Count = 1 });
				}
				else
				{
					jtHistory.First(j => j.Type == firstType).Count++;
				}
			}

			return jtHistory;
		}

		private string PickJobType(HashSet<string>? exclude)
		{
			var choices = JobTypeDistribution
				.Where(kv => exclude == null || !exclude.Contains(kv.Key))
				.ToList();

			double total = choices.Sum(kv => kv.Value);
			double pick = _random.NextDouble() * total;
			double accum = 0;

			foreach (var (type, weight) in choices)
			{
				accum += weight;
				if (pick <= accum)
					return type;
			}

			return choices.Last().Key;
		}

		private void ApplyTraits(Student student, string jobType, bool isPositive, Dictionary<string, List<Job>> jobsByType)
		{
			if (!jobsByType.TryGetValue(jobType, out var jobs) || jobs.Count == 0)
				return;

			var job = jobs[_random.Next(jobs.Count)];
			if (job.RequiredTraits == null)
				return;

			foreach (var traitName in job.RequiredTraits)
			{
				var existing = student.Traits!.FirstOrDefault(t => t.Name == traitName);
				if (existing == null)
				{
					student.Traits!.Add(new Trait
					{
						Name = traitName,
						Positive = isPositive ? 1 : 0,
						Negative = isPositive ? 0 : 1
					});
				}
				else
				{
					if (isPositive) existing.Positive++;
					else existing.Negative++;
				}
			}
		}

		private static int GetHistoryIndex(List<JobTypeHistory> jtHistory, int expIndex)
		{
			int cum = 0;
			for (int i = 0; i < jtHistory.Count; i++)
			{
				cum += jtHistory[i].Count;
				if (expIndex < cum) return i;
			}
			return jtHistory.Count - 1;
		}

		private async Task CreateCompanyRatingsAsync()
		{
			var companiesWithoutRanking = await _companies.Find(c => c.ThumbsReceived == null).ToListAsync();
			int totalCompanies = companiesWithoutRanking.Count;

			int avgCount = (int)(totalCompanies * 0.7);
			int lowCount = (int)(totalCompanies * 0.2);

			var shuffled = companiesWithoutRanking.OrderBy(_ => _random.Next()).ToList();
			var avgGroup = shuffled.Take(avgCount);
			var lowGroup = shuffled.Skip(avgCount).Take(lowCount);
			var highGroup = shuffled.Skip(avgCount + lowCount);

			await AssignRatingsAsync(avgGroup, 3.0, 4.0);
			await AssignRatingsAsync(lowGroup, 1.0, 2.99);
			await AssignRatingsAsync(highGroup, 4.01, 5.0);
		}

		private async Task AssignRatingsAsync(IEnumerable<Company> companies, double minStar, double maxStar)
		{
			foreach (var company in companies)
			{
				int totalRatings = _random.Next(1, 501);
				double starAvg = _random.NextDouble() * (maxStar - minStar) + minStar;
				int up = (int)Math.Round((starAvg / 5.0) * totalRatings);
				int down = totalRatings - up;

				company.ThumbsReceived = new ThumbsReceived { Up = up, Down = down };
				await _companies.ReplaceOneAsync(c => c.CompanyId == company.CompanyId, company);
			}
		}

		private async Task ScrapeNewJobs()
		{
			await _scraper.ScrapeAllPagesAsync();
		}

		private async Task CreateApplicationsAsync(bool isFirstTime)
		{
			var applications = await _applications.Find(_ => true).ToListAsync();
			var allStudents = await _students.Find(_ => true).ToListAsync();

			var jobs = isFirstTime
				? await _jobs.Find(_ => true).ToListAsync()
				: await GetJobsWithoutApplicationsAsync(applications);

			if (jobs.Count == 0)
				return;

			var newApplications = new List<Application>();

			foreach (var job in jobs)
			{
				int applicationCount = _random.Next(10, 21);
				int sameTypeCount = (int)Math.Round(applicationCount * 0.6);
				int randomCount = applicationCount - sameTypeCount;

				var experienced = allStudents
					.Where(s => s.JobTypesHistory?.Any(h => h.Type == job.Type) == true)
					.OrderBy(_ => _random.Next())
					.Take(sameTypeCount)
					.ToList();

				var remainingPool = allStudents
					.Except(experienced)
					.OrderBy(_ => _random.Next())
					.Take(randomCount);

				foreach (var student in experienced.Concat(remainingPool))
				{
					newApplications.Add(new Application
					{
						ApplicationId = ObjectId.GenerateNewId(),
						StudentId = student.StudentId,
						JobId = job.JobId,
						Status = "pending"
					});
				}
			}

			await _applications.InsertManyAsync(newApplications);
		}

		private async Task<List<Job>> GetJobsWithoutApplicationsAsync(List<Application> applications)
		{
			var jobIdsWithApplications = applications.Select(a => a.JobId).Distinct().ToHashSet();
			return await _jobs.Find(j => !jobIdsWithApplications.Contains(j.JobId)).ToListAsync();
		}

		private async Task UpdateApplicationsAsync(bool isFirstTime)
		{
			var today = DateTime.Today;
			var applications = await _applications.Find(_ => true).ToListAsync();
			var jobs = await _jobs.Find(j => applications.Select(a => a.JobId).Contains(j.JobId)).ToListAsync();
			var students = await _students.Find(_ => true).ToListAsync();

			foreach (var job in jobs)
			{
				var jobApps = applications.Where(a => a.JobId == job.JobId).ToList();
				await ProcessJobApplicationsAsync(job, jobApps, students, today, isFirstTime);
			}
		}

		private async Task ProcessJobApplicationsAsync(Job job, List<Application> jobApps, List<Student> students, DateTime today, bool isFirstTime)
		{
			if (isFirstTime)
			{
				if (today >= job.StartDate.AddDays(1).Date && today <= job.EndDate.Date)
				{
					await HireAndUpdateApplicationsAsync(job, jobApps, "hired");
				}
				else if (today > job.EndDate.Date)
				{
					await HireAndUpdateApplicationsAsync(job, jobApps, "finished");
					await UpdateStudentHistoriesAndRatingsAsync(job, jobApps, students);
				}
			}
			else
			{
				var hasHiredStatus = jobApps.Any(a => a.Status == "hired");
				var hasPendingStatus = jobApps.Any(a => a.Status == "pending");

				if (today >= job.StartDate.AddDays(1).Date && today <= job.EndDate.Date)
				{
					if (!hasHiredStatus)
					{
						await HireAndUpdateApplicationsAsync(job, jobApps, "hired");
					}
					else if (hasHiredStatus && hasPendingStatus)
					{
						var expiredApps = jobApps.Where(a => a.Status == "pending").ToList();
						expiredApps.ForEach(a => a.Status = "expired");
						await UpdateApplicationsInDbAsync(expiredApps);
					}
				}
				else if (today > job.EndDate.Date)
				{
					if (!hasHiredStatus && !hasPendingStatus)
						return;

					if (hasPendingStatus)
						await HireAndUpdateApplicationsAsync(job, jobApps, "hired");

					var hiredApps = jobApps.Where(a => a.Status == "hired").ToList();
					hiredApps.ForEach(a => a.Status = "finished");
					await UpdateApplicationsInDbAsync(hiredApps);
					await UpdateStudentHistoriesAndRatingsAsync(job, hiredApps, students);
				}
			}
		}

		private async Task HireAndUpdateApplicationsAsync(Job job, List<Application> jobApps, string finalStatus)
		{
			int hireCount = GetHireCount();
			var recommendations = await _recommender.RecommendStudentsForJobAsync(job.JobId.ToString());
			var topIds = recommendations
				.Take(hireCount)
				.Select(dto => ObjectId.Parse(dto.StudentBasicInfo!.StudentId))
				.ToHashSet();

			foreach (var app in jobApps)
			{
				app.Status = topIds.Contains(app.StudentId) ? finalStatus : "expired";
			}

			await UpdateApplicationsInDbAsync(jobApps);
		}

		private int GetHireCount() => _random.NextDouble() switch
		{
			< 0.7 => 1,
			< 0.9 => 2,
			_ => 3
		};

		private async Task UpdateApplicationsInDbAsync(List<Application> apps)
		{
			foreach (var app in apps)
			{
				await _applications.ReplaceOneAsync(a => a.ApplicationId == app.ApplicationId, app);
			}
		}

		private async Task UpdateStudentHistoriesAndRatingsAsync(Job job, List<Application> apps, List<Student> students)
		{
			foreach (var app in apps)
			{
				var student = students.First(s => s.StudentId == app.StudentId);
				var history = student.JobTypesHistory ??= new List<JobTypeHistory>();
				var historyItem = history.FirstOrDefault(h => h.Type == job.Type);

				if (historyItem != null)
					historyItem.Count++;
				else
					history.Add(new JobTypeHistory { Type = job.Type, Count = 1 });

				await _students.ReplaceOneAsync(s => s.StudentId == student.StudentId, student);
				await ApplyRatingsAsync(app, job);
			}
		}

		private async Task ApplyRatingsAsync(Application app, Job job)
		{
			double roll = _random.NextDouble();

			if (roll < 0.4)
			{
				app.Status = "ratedByStudent";
				await UpdateCompanyThumbsAsync(job.CompanyId, 0.7);

				if (_random.NextDouble() < 0.5)
				{
					app.Status = "rated";
					await UpdateStudentThumbsAsync(app.StudentId, job.Type, 0.85);
				}
			}
			else if (roll < 0.7)
			{
				app.Status = "ratedByCompany";
				await UpdateStudentThumbsAsync(app.StudentId, job.Type, 0.85);

				if (_random.NextDouble() < 0.6)
				{
					app.Status = "rated";
					await UpdateCompanyThumbsAsync(job.CompanyId, 0.7);
				}
			}

			await _applications.ReplaceOneAsync(a => a.ApplicationId == app.ApplicationId, app);
		}

		private async Task UpdateCompanyThumbsAsync(ObjectId companyId, double upChance)
		{
			var company = await _companies.Find(c => c.CompanyId == companyId).FirstOrDefaultAsync();
			company.ThumbsReceived ??= new ThumbsReceived();

			if (_random.NextDouble() < upChance)
				company.ThumbsReceived.Up++;
			else
				company.ThumbsReceived.Down++;

			await _companies.ReplaceOneAsync(c => c.CompanyId == companyId, company);
		}

		private async Task UpdateStudentThumbsAsync(ObjectId studentId, string jobType, double upChance)
		{
			var student = await _students.Find(s => s.StudentId == studentId).FirstOrDefaultAsync();
			student.ThumbsReceived ??= new ThumbsReceived();

			bool isUp = _random.NextDouble() < upChance;
			if (isUp)
				student.ThumbsReceived.Up++;
			else
				student.ThumbsReceived.Down++;

			student.Traits ??= new List<Trait>();

			var job = await _jobs.Find(j => j.Type == jobType).FirstOrDefaultAsync();
			if (job?.RequiredTraits != null)
			{
				foreach (var trait in job.RequiredTraits)
				{
					var traitEntry = student.Traits.FirstOrDefault(t => t.Name == trait);
					if (traitEntry == null)
					{
						student.Traits.Add(new Trait
						{
							Name = trait,
							Positive = isUp ? 1 : 0,
							Negative = isUp ? 0 : 1
						});
					}
					else
					{
						if (isUp) traitEntry.Positive++;
						else traitEntry.Negative++;
					}
				}
			}

			await _students.ReplaceOneAsync(s => s.StudentId == studentId, student);
		}
	}
}