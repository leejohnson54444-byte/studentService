namespace StudentService.Domain.DTOs
{
	public class StudentUpdate
	{
		public required string ApplicationId { get; set; }
		public bool? IsThumbUp { get; set; }

		// there is no JobTypeHistoryUpdate bc it is updated automatically through DataSeeder 
		// Traits get updated automatically as well in Services.StudentService class
	}
}
