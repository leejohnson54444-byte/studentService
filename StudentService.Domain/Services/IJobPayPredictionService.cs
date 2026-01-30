using StudentService.Domain.Services.Models;
using StudentService.Domain.ValueObjects;

namespace StudentService.Domain.Services
{
    public interface IJobPayPredictionService
    {
        Task<JobPayPredictionResult> EvaluateAsync(string algorithm);
        Task<float> PredictAsync(string algorithm, JobPayPredictionInput input);
        Task<AllJobPayPredictionMetricsResult> EvaluateAllAlgorithmsAsync();
    }
}
