using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.Repositories;
using StudentService.Domain.Services;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Services
{
	public class JobService : IJobService
	{
		private readonly MongoDbContext _context;
		private readonly IApplicationRepository _applicationRepository;

		public JobService(MongoDbContext context, IApplicationRepository applicationRepository)
		{
			_context = context;
			_applicationRepository = applicationRepository;
		}

		public async Task UpdateRatingByIdAsync(string jobId, string studentId, bool isPositiveRating)
		{
			var jobOid = ParseObjectId(jobId, nameof(jobId));
			var studentOid = ParseObjectId(studentId, nameof(studentId));

			var jobQuery = _context.GetCollection<Job>("jobs");
			var studentQuery = _context.GetCollection<Student>("students");
			var applicationQuery = _context.GetCollection<Application>("applications");
			var companyQuery = _context.GetCollection<Company>("companies");

			var job = await jobQuery.Find(j => j.JobId == jobOid).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Job '{jobOid}' not found");

			var student = await studentQuery.Find(s => s.StudentId == studentOid).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Student '{studentOid}' not found");

			var application = await applicationQuery.Find(a => a.JobId == jobOid && a.StudentId == studentOid).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Application for student '{studentOid}' and job '{jobOid}' not found");

			await _applicationRepository.UpdateStatusAsync(application.ApplicationId.ToString(), null, false, true);

			var company = await companyQuery.Find(c => c.CompanyId == job.CompanyId).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Company '{job.CompanyId}' not found");

			company.ThumbsReceived ??= new ThumbsReceived();

			if (isPositiveRating)
				company.ThumbsReceived.Up++;
			else
				company.ThumbsReceived.Down++;

			await companyQuery.ReplaceOneAsync(c => c.CompanyId == job.CompanyId, company);
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}
}
