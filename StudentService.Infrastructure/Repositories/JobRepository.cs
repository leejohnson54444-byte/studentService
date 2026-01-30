using AutoMapper;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Repositories
{
	public class JobRepository : IJobRepository
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;

		public JobRepository(MongoDbContext context, IMapper mapper)
		{
			_context = context;
			_mapper = mapper;
		}

		public async Task<IEnumerable<JobBasicInfo>> GetAllAsync()
		{
			var jobs = await _context.GetCollection<Job>("jobs")
				.Find(_ => true)
				.ToListAsync();

			return jobs.Select(j => _mapper.Map<JobBasicInfo>(j));
		}

		public async Task<IEnumerable<JobBasicInfo>> GetByCompanyIdAsync(string companyId)
		{
			var oid = ParseObjectId(companyId, nameof(companyId));

			var jobs = await _context.GetCollection<Job>("jobs")
				.Find(j => j.CompanyId == oid)
				.ToListAsync();

			return jobs.Select(j => _mapper.Map<JobBasicInfo>(j));
		}

		public async Task<JobBasicInfo> GetBasicByIdAsync(string jobId)
		{
			var job = await GetJobByIdAsync(jobId);
			return _mapper.Map<JobBasicInfo>(job);
		}

		public async Task<JobDetailInfo> GetDetailedByIdAsync(string jobId)
		{
			var job = await GetJobByIdAsync(jobId);
			var jobDetail = _mapper.Map<JobDetailInfo>(job);

			// Fetch company name
			var company = await _context.GetCollection<Company>("companies")
				.Find(c => c.CompanyId == job.CompanyId)
				.FirstOrDefaultAsync();

			jobDetail.CompanyName = company?.Name;

			return jobDetail;
		}

		private async Task<Job> GetJobByIdAsync(string jobId)
		{
			var oid = ParseObjectId(jobId, nameof(jobId));

			return await _context.GetCollection<Job>("jobs")
				.Find(j => j.JobId == oid)
				.SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Job '{jobId}' not found");
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}
}
