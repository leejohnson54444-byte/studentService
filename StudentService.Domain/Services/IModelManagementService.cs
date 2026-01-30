namespace StudentService.Domain.Services
{
	public interface IModelManagementService
	{
		Task<ModelTrainingResultDto> TrainAllModelsAsync();
		Task<ModelTrainingResultDto> TrainModelAsync(string modelType);
		Task<List<ModelVersionDto>> GetModelVersionsAsync(string modelType);
		Task<ModelVersionDto?> GetProductionModelAsync(string modelType);
		Task<bool> PromoteToProductionAsync(string modelType, string version);
		Task<bool> RollbackProductionModelAsync(string modelType);
		TrainingStatusDto GetTrainingStatus();
		void InvalidateModelCache(string? modelType = null);
	}

	public class ModelTrainingResultDto
	{
		public bool Success { get; set; }
		public string? RunId { get; set; }
		public Dictionary<string, double>? Metrics { get; set; }
		public bool PromotedToProduction { get; set; }
		public string? Message { get; set; }
		public string? PreviousProductionRunId { get; set; }
		public List<ModelTrainingResultDto>? AllResults { get; set; }
	}

	public class ModelVersionDto
	{
		public string? RunId { get; set; }
		public string? Version { get; set; }
		public string Stage { get; set; } = "None";
		public DateTime CreatedAt { get; set; }
		public Dictionary<string, double>? Metrics { get; set; }
		public string? ComparisonResult { get; set; }
		public string? PreviousProductionRunId { get; set; }
	}

	public class TrainingStatusDto
	{
		public bool IsTraining { get; set; }
		public bool IsEnabled { get; set; }
		public string ScheduledTime { get; set; } = string.Empty;
		public DateTime NextRunTime { get; set; }
	}
}
