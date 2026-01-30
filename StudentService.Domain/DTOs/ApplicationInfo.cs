namespace StudentService.Domain.DTOs
{
	public class ApplicationInfo
	{
		public required string ApplicationId { get; set; }
		public StudentBasicInfo? StudentBasicInfo { get; set; }
		public JobBasicInfo? JobBasicInfo { get; set; }
		public required string Status { get; set; }
	}
}
