using AutoMapper;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Repositories
{
	public class StudentRepository : IStudentRepository
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;

		public StudentRepository(MongoDbContext context, IMapper mapper)
		{
			_context = context;
			_mapper = mapper;
		}

		public async Task<StudentBasicInfo> GetByApplicationIdAsync(string applicationId)
		{
			var oid = ParseObjectId(applicationId, nameof(applicationId));

			var application = await _context.GetCollection<Application>("applications")
				.Find(a => a.ApplicationId == oid)
				.SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Application '{oid}' not found");

			var student = await _context.GetCollection<Student>("students")
				.Find(s => s.StudentId == application.StudentId)
				.SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Student '{application.StudentId}' not found");

			return _mapper.Map<StudentBasicInfo>(student);
		}

		public async Task<StudentDetailInfo> GetDetailedInfoByIdAsync(string studentId)
		{
			var student = await GetStudentByIdAsync(studentId);
			return _mapper.Map<StudentDetailInfo>(student);
		}

		public async Task<StudentBasicInfo> GetBasicInfoByIdAsync(string studentId)
		{
			var student = await GetStudentByIdAsync(studentId);
			return _mapper.Map<StudentBasicInfo>(student);
		}

		private async Task<Student> GetStudentByIdAsync(string studentId)
		{
			var oid = ParseObjectId(studentId, nameof(studentId));

			return await _context.GetCollection<Student>("students")
				.Find(s => s.StudentId == oid)
				.SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Student '{studentId}' not found");
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}
}
