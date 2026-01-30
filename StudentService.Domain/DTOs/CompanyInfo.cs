namespace StudentService.Domain.DTOs
{
	public class CompanyInfo
	{
		public required string Name { get; set; }
		public int? ThumbsUp { get; set; }
		public int? ThumbsDown { get; set; }
	}
}
