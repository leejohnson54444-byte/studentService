using AutoMapper;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Domain.Services;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Services
{
	public class ApplicationService : IApplicationService
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;
		private readonly IApplicationRepository _applicationRepository;

		public ApplicationService(MongoDbContext context, IMapper mapper, IApplicationRepository applicationRepository)
		{
			_context = context;
			_mapper = mapper;
			_applicationRepository = applicationRepository;
		}

		public async Task CreateAsync(ApplicationCreate applicationCreate)
		{
			var studentOid = ParseObjectId(applicationCreate.StudentId, nameof(applicationCreate.StudentId));
			var jobOid = ParseObjectId(applicationCreate.JobId, nameof(applicationCreate.JobId));

			var existingApplications = await _applicationRepository.GetByJobIdAsync(applicationCreate.JobId);

			if (existingApplications.Any(a => a.StudentBasicInfo?.StudentId == applicationCreate.StudentId))
				throw new InvalidOperationException($"Application already exists for StudentId '{applicationCreate.StudentId}' and JobId '{applicationCreate.JobId}'.");

			var application = _mapper.Map<Application>(applicationCreate);
			await _context.GetCollection<Application>("applications").InsertOneAsync(application);
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}
}
