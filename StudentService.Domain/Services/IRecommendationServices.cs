using StudentService.Domain.DTOs;

namespace StudentService.Domain.Services
{
    public interface IJobRecommendationService
    {
        Task<IReadOnlyList<JobRecommendationResult>> RecommendJobsForStudentAsync(string studentId);
        Task<IReadOnlyList<JobRecommendationResult>> MLRecommendJobsForStudentAsync(string studentId);
	}

    public interface IStudentRecommendationService
    {
        Task<IReadOnlyList<ApplicationInfo>> RecommendStudentsForJobAsync(string jobId);
        Task<IReadOnlyList<StudentRecommendationResult>> MLRecommendStudentsForJobAsync(string jobId);
	}
}
