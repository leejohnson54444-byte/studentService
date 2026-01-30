namespace StudentService.Domain.DTOs
{
	public class RecommendationExplanation
	{
		public required string FeatureName { get; set; }
		public required string Description { get; set; }
		public float Value { get; set; }
		public float Contribution { get; set; }
	}

	public class StudentRecommendationResult
	{
		public required ApplicationInfo Application { get; set; }
		public float Score { get; set; }
		public List<RecommendationExplanation> Explanations { get; set; } = new();
	}

	public class JobRecommendationResult
	{
		public required JobBasicInfo Job { get; set; }
		public float Score { get; set; }
		public List<RecommendationExplanation> Explanations { get; set; } = new();
	}
}
