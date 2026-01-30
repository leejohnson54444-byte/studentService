namespace StudentService.Domain.Services
{
	public interface IJobService
	{
		Task UpdateRatingByIdAsync(string jobId, string studentId, bool isPositiveRating);
	}
}
