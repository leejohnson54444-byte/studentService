using AutoMapper;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Repositories
{
	public class ApplicationRepository : IApplicationRepository
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;
		private readonly IStudentRepository _studentRepository;
		private readonly IJobRepository _jobRepository;

		public ApplicationRepository(
			MongoDbContext context,
			IMapper mapper,
			IStudentRepository studentRepository,
			IJobRepository jobRepository)
		{
			_context = context;
			_mapper = mapper;
			_studentRepository = studentRepository;
			_jobRepository = jobRepository;
		}

		public async Task<IEnumerable<ApplicationInfo>> GetByJobIdAsync(string jobId)
		{
			var oid = ParseObjectId(jobId, nameof(jobId));

			var applications = await _context.GetCollection<Application>("applications")
				.Find(a => a.JobId == oid)
				.ToListAsync();

			var result = new List<ApplicationInfo>();
			foreach (var application in applications)
			{
				if (application.Status != "finished" && application.Status != "ratedByStudent")
					continue;

				var applicationDto = _mapper.Map<ApplicationInfo>(application);
				applicationDto.StudentBasicInfo = await _studentRepository.GetByApplicationIdAsync(application.ApplicationId.ToString());
				result.Add(applicationDto);
			}

			return result;
		}

		public async Task<IEnumerable<ApplicationInfo>> GetByStudentIdAsync(string studentId)
		{
			var oid = ParseObjectId(studentId, nameof(studentId));

			var applications = await _context.GetCollection<Application>("applications")
				.Find(a => a.StudentId == oid)
				.ToListAsync();

			var result = new List<ApplicationInfo>();
			foreach (var application in applications)
			{
				if (application.Status != "finished" && application.Status != "ratedByCompany")
					continue;

				var applicationDto = _mapper.Map<ApplicationInfo>(application);
				applicationDto.JobBasicInfo = await _jobRepository.GetBasicByIdAsync(application.JobId.ToString());
				result.Add(applicationDto);
			}

			return result;
		}

		public async Task<ApplicationInfo> UpdateStatusAsync(string id, string? hiringStatus, bool isCompanyRating, bool isStudentRating)
		{
			var oid = ParseObjectId(id, nameof(id));

			if (hiringStatus == null && !isCompanyRating && !isStudentRating)
				throw new ArgumentException("At least one status update parameter must be provided.");

			var applications = _context.GetCollection<Application>("applications");
			var application = await applications.Find(a => a.ApplicationId == oid).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Application '{oid}' not found");

			UpdateApplicationStatus(application, hiringStatus, isCompanyRating, isStudentRating);

			await applications.ReplaceOneAsync(a => a.ApplicationId == oid, application);

			var applicationDto = _mapper.Map<ApplicationInfo>(application);
			applicationDto.StudentBasicInfo = await _studentRepository.GetByApplicationIdAsync(application.ApplicationId.ToString());

			return applicationDto;
		}

		private static void UpdateApplicationStatus(Application application, string? hiringStatus, bool isCompanyRating, bool isStudentRating)
		{
			if (hiringStatus != null)
			{
				if (hiringStatus != "hired" && hiringStatus != "expired")
					throw new ArgumentException("Status must be either 'hired' or 'expired'.");

				if (application.Status != "pending")
					throw new InvalidOperationException($"Cannot change status because current status is '{application.Status}', not 'pending'.");

				application.Status = hiringStatus;
			}
			else if (isCompanyRating)
			{
				if (application.Status != "finished" && application.Status != "ratedByStudent")
					throw new InvalidOperationException($"Cannot change status because current status is '{application.Status}'.");

				application.Status = application.Status == "finished" ? "ratedByCompany" : "rated";
			}
			else if (isStudentRating)
			{
				if (application.Status != "finished" && application.Status != "ratedByCompany")
					throw new InvalidOperationException($"Cannot change status because current status is '{application.Status}'.");

				application.Status = application.Status == "finished" ? "ratedByStudent" : "rated";
			}
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}
}
