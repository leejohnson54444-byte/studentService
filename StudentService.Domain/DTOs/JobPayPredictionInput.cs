namespace StudentService.Domain.ValueObjects
{
    public class JobPayPredictionInput
    {
        public string Type { get; set; } = string.Empty;
        public string PlaceOfWork { get; set; } = string.Empty;
        public List<string> RequiredTraits { get; set; } = new();
    }

    public class JobPayPredictionResult
    {
        public float PredictedPay { get; set; }
        public float[] Predictions { get; set; } = Array.Empty<float>();
        public float[] Actuals { get; set; } = Array.Empty<float>();
        public float MAE { get; set; }
    }
}
