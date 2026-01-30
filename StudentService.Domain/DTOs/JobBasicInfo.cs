namespace StudentService.Domain.DTOs
{
	public class JobBasicInfo
	{
		public required string Id { get; set; }
		public required string Title { get; set; }
		public required DateTime StartDate { get; set; }
		public required DateTime EndDate { get; set; }
		public required string PlaceOfWork { get; set; }	
		public string? Description { get; set; }
	}
}
