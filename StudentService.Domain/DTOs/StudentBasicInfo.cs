namespace StudentService.Domain.DTOs
{
	public class StudentBasicInfo
	{
		public required string StudentId { get; set; }
		public required string FirstName { get; set; }
		public required string LastName { get; set; }
		public double? Rating { get; set; }
	}
}
