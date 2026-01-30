namespace StudentService.Domain.DTOs
{
	public class StudentDetailInfo
	{
		public required string StudentId { get; set; }
		public required string FirstName { get; set; }
		public required string LastName { get; set; }
		public double? Rating { get; set; }
		public List<TraitInfo>? Traits { get; set; }
		public List<JobTypeHistoryInfo>? JobTypesHistory { get; set; }
	}
}
