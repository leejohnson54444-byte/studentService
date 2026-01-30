using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Domain.Services;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Services
{
	public class StudentApplicationService : IStudentApplicationService
	{
		private readonly IApplicationRepository _applicationRepository;
		private readonly MongoDbContext _context;

		public StudentApplicationService(IApplicationRepository applicationRepository, MongoDbContext context)
		{
			_applicationRepository = applicationRepository;
			_context = context;
		}

		public async Task UpdateRatingByIdAsync(StudentUpdate studentUpdateDto)
		{
			var applicationOid = ParseObjectId(studentUpdateDto.ApplicationId, nameof(studentUpdateDto.ApplicationId));

			if (studentUpdateDto.IsThumbUp == null)
				throw new ArgumentException("No rating was given.");

			var applicationQuery = _context.GetCollection<Application>("applications");
			var studentQuery = _context.GetCollection<Student>("students");
			var jobQuery = _context.GetCollection<Job>("jobs");

			var application = await applicationQuery.Find(a => a.ApplicationId == applicationOid).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Application '{studentUpdateDto.ApplicationId}' not found");

			var student = await studentQuery.Find(s => s.StudentId == application.StudentId).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Student '{application.StudentId}' not found");

			var job = await jobQuery.Find(j => j.JobId == application.JobId).SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Job '{application.JobId}' not found");

			await _applicationRepository.UpdateStatusAsync(studentUpdateDto.ApplicationId, null, true, false);

			UpdateStudentRating(student, studentUpdateDto.IsThumbUp.Value);
			UpdateStudentTraits(student, job.RequiredTraits, studentUpdateDto.IsThumbUp.Value);

			await studentQuery.ReplaceOneAsync(s => s.StudentId == application.StudentId, student);
		}

		private static void UpdateStudentRating(Student student, bool isThumbUp)
		{
			student.ThumbsReceived ??= new ThumbsReceived();

			if (isThumbUp)
				student.ThumbsReceived.Up++;
			else
				student.ThumbsReceived.Down++;
		}

		private static void UpdateStudentTraits(Student student, List<string>? requiredTraits, bool isThumbUp)
		{
			if (requiredTraits == null || requiredTraits.Count == 0)
				return;

			student.Traits ??= new List<Trait>();

			foreach (var traitName in requiredTraits)
			{
				var existing = student.Traits.FirstOrDefault(t => t.Name == traitName);
				if (existing == null)
				{
					student.Traits.Add(new Trait
					{
						Name = traitName,
						Positive = isThumbUp ? 1 : 0,
						Negative = isThumbUp ? 0 : 1
					});
				}
				else
				{
					if (isThumbUp)
						existing.Positive = (existing.Positive ?? 0) + 1;
					else
						existing.Negative = (existing.Negative ?? 0) + 1;
				}
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
