namespace StudentService.Domain.DTOs
{
	public class JobDetailInfo
	{
		public required string Title { get; set; }
		public required string Type { get; set; } 
		public required double HourlyPay { get; set; }
		public required DateTime StartDate { get; set; }
		public required DateTime EndDate { get; set; } 
		public required string PlaceOfWork { get; set; }
		public List<string>? RequiredTraits { get; set; }
		public string? Description { get; set; }
		public string? CompanyName { get; set; }
	}
}
