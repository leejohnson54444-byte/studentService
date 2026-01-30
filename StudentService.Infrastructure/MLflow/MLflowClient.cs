using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace StudentService.Infrastructure.MLflow
{
	public class MLflowClient
	{
		private readonly HttpClient _httpClient;
		private readonly ILogger<MLflowClient> _logger;
		private readonly string _trackingUri;

		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true
		};

		public MLflowClient(HttpClient httpClient, ILogger<MLflowClient> logger, string trackingUri)
		{
			_httpClient = httpClient;
			_logger = logger;
			_trackingUri = trackingUri.TrimEnd('/');
			
			if (_httpClient.BaseAddress == null)
			{
				_httpClient.BaseAddress = new Uri(_trackingUri);
			}
		}

		private async Task<HttpResponseMessage> PostJsonAsync<T>(string url, T content)
		{
			var json = JsonSerializer.Serialize(content, JsonOptions);
			_logger.LogDebug("POST {Url} with body: {Body}", url, json);
			
			var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
			var response = await _httpClient.PostAsync(url, httpContent);
			
			if (!response.IsSuccessStatusCode)
			{
				var errorBody = await response.Content.ReadAsStringAsync();
				_logger.LogError("MLflow API error: {StatusCode} - {Body}", response.StatusCode, errorBody);
			}
			
			return response;
		}

		public async Task<string> GetOrCreateExperimentAsync(string experimentName)
		{
			try
			{
				var response = await _httpClient.GetAsync($"/api/2.0/mlflow/experiments/get-by-name?experiment_name={Uri.EscapeDataString(experimentName)}");
				
				if (response.IsSuccessStatusCode)
				{
					var content = await response.Content.ReadAsStringAsync();
					var result = JsonSerializer.Deserialize<GetExperimentResponse>(content, JsonOptions);
					return result?.Experiment?.ExperimentId ?? throw new Exception("Failed to get experiment ID");
				}

				var createRequest = new CreateExperimentRequest { Name = experimentName };
				var createResponse = await PostJsonAsync("/api/2.0/mlflow/experiments/create", createRequest);
				createResponse.EnsureSuccessStatusCode();
				
				var createContent = await createResponse.Content.ReadAsStringAsync();
				var createResult = JsonSerializer.Deserialize<CreateExperimentResponse>(createContent, JsonOptions);
				_logger.LogInformation("Created MLflow experiment: {ExperimentName} with ID: {ExperimentId}", 
					experimentName, createResult?.ExperimentId);
				
				return createResult?.ExperimentId ?? throw new Exception("Failed to create experiment");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to get or create experiment: {ExperimentName}", experimentName);
				throw;
			}
		}

		public async Task<MLflowRun> CreateRunAsync(string experimentId, string runName, Dictionary<string, string>? tags = null)
		{
			var allTags = new List<CreateRunTag>
			{
				new() { Key = "mlflow.runName", Value = runName }
			};

			if (tags != null)
			{
				allTags.AddRange(tags.Select(t => new CreateRunTag { Key = t.Key, Value = t.Value }));
			}

			var request = new CreateRunRequest
			{
				ExperimentId = experimentId,
				StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Tags = allTags
			};

			var response = await PostJsonAsync("/api/2.0/mlflow/runs/create", request);
			response.EnsureSuccessStatusCode();

			var content = await response.Content.ReadAsStringAsync();
			var result = JsonSerializer.Deserialize<CreateRunResponse>(content, JsonOptions);
			_logger.LogInformation("Created MLflow run: {RunId}", result?.Run?.Info?.RunId);
			
			return result?.Run ?? throw new Exception("Failed to create run");
		}

		public async Task LogParamAsync(string runId, string key, string value)
		{
			var request = new LogParamRequest { RunId = runId, Key = key, Value = value };
			var response = await PostJsonAsync("/api/2.0/mlflow/runs/log-parameter", request);
			response.EnsureSuccessStatusCode();
		}

		public async Task LogParamsAsync(string runId, Dictionary<string, string> parameters)
		{
			foreach (var param in parameters)
			{
				await LogParamAsync(runId, param.Key, param.Value);
			}
		}

		public async Task LogMetricAsync(string runId, string key, double value, long? step = null)
		{
			var request = new LogMetricRequest
			{
				RunId = runId,
				Key = key,
				Value = value,
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Step = step ?? 0
			};
			var response = await PostJsonAsync("/api/2.0/mlflow/runs/log-metric", request);
			response.EnsureSuccessStatusCode();
		}

		public async Task LogMetricsAsync(string runId, Dictionary<string, double> metrics)
		{
			foreach (var metric in metrics)
			{
				await LogMetricAsync(runId, metric.Key, metric.Value);
			}
		}

		public async Task FinishRunAsync(string runId, string status = "FINISHED")
		{
			var request = new UpdateRunRequest
			{
				RunId = runId,
				Status = status,
				EndTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			};
			var response = await PostJsonAsync("/api/2.0/mlflow/runs/update", request);
			response.EnsureSuccessStatusCode();
			_logger.LogInformation("Finished MLflow run: {RunId} with status: {Status}", runId, status);
		}

		public async Task<MLflowRun?> GetRunAsync(string runId)
		{
			var response = await _httpClient.GetAsync($"/api/2.0/mlflow/runs/get?run_id={runId}");
			if (!response.IsSuccessStatusCode) return null;
			
			var content = await response.Content.ReadAsStringAsync();
			var result = JsonSerializer.Deserialize<GetRunResponse>(content, JsonOptions);
			return result?.Run;
		}

		public async Task<List<MLflowRun>> SearchRunsAsync(string experimentId, string? filter = null, int maxResults = 100)
		{
			var request = new SearchRunsRequest
			{
				ExperimentIds = new[] { experimentId },
				FilterString = filter ?? "",
				MaxResults = maxResults,
				OrderBy = new[] { "start_time DESC" }
			};

			var response = await PostJsonAsync("/api/2.0/mlflow/runs/search", request);
			response.EnsureSuccessStatusCode();

			var content = await response.Content.ReadAsStringAsync();
			var result = JsonSerializer.Deserialize<SearchRunsResponse>(content, JsonOptions);
			return result?.Runs ?? new List<MLflowRun>();
		}

		public async Task<RegisteredModel> CreateRegisteredModelAsync(string modelName, string? description = null)
		{
			try
			{
				var request = new CreateRegisteredModelRequest { Name = modelName, Description = description };
				var response = await PostJsonAsync("/api/2.0/mlflow/registered-models/create", request);
				
				if (response.IsSuccessStatusCode)
				{
					var content = await response.Content.ReadAsStringAsync();
					var result = JsonSerializer.Deserialize<CreateRegisteredModelResponse>(content, JsonOptions);
					_logger.LogInformation("Created registered model: {ModelName}", modelName);
					return result?.RegisteredModel ?? throw new Exception("Failed to create registered model");
				}

				return await GetRegisteredModelAsync(modelName) ?? throw new Exception($"Failed to get or create model: {modelName}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to create registered model: {ModelName}", modelName);
				throw;
			}
		}

		public async Task<RegisteredModel?> GetRegisteredModelAsync(string modelName)
		{
			var response = await _httpClient.GetAsync($"/api/2.0/mlflow/registered-models/get?name={Uri.EscapeDataString(modelName)}");
			if (!response.IsSuccessStatusCode) return null;

			var content = await response.Content.ReadAsStringAsync();
			var result = JsonSerializer.Deserialize<GetRegisteredModelResponse>(content, JsonOptions);
			return result?.RegisteredModel;
		}

		public async Task<ModelVersion> CreateModelVersionAsync(string modelName, string runId, string? description = null)
		{
			var request = new CreateModelVersionRequest
			{
				Name = modelName,
				Source = $"runs:/{runId}/model",
				RunId = runId,
				Description = description
			};

			var response = await PostJsonAsync("/api/2.0/mlflow/model-versions/create", request);
			response.EnsureSuccessStatusCode();

			var content = await response.Content.ReadAsStringAsync();
			var result = JsonSerializer.Deserialize<CreateModelVersionResponse>(content, JsonOptions);
			_logger.LogInformation("Created model version {Version} for model: {ModelName}", 
				result?.ModelVersion?.Version, modelName);
			
			return result?.ModelVersion ?? throw new Exception("Failed to create model version");
		}

		public async Task TransitionModelVersionStageAsync(string modelName, string version, string stage)
		{
			var request = new TransitionStageRequest
			{
				Name = modelName,
				Version = version,
				Stage = stage,
				ArchiveExistingVersions = stage == "Production"
			};

			var response = await PostJsonAsync("/api/2.0/mlflow/model-versions/transition-stage", request);
			response.EnsureSuccessStatusCode();
			_logger.LogInformation("Transitioned model {ModelName} version {Version} to stage: {Stage}", 
				modelName, version, stage);
		}

		public async Task<List<ModelVersion>> GetLatestModelVersionsAsync(string modelName, string[]? stages = null)
		{
			var url = $"/api/2.0/mlflow/registered-models/get-latest-versions?name={Uri.EscapeDataString(modelName)}";
			if (stages != null && stages.Length > 0)
			{
				url += "&" + string.Join("&", stages.Select(s => $"stages={Uri.EscapeDataString(s)}"));
			}

			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode) return new List<ModelVersion>();

			var content = await response.Content.ReadAsStringAsync();
			var result = JsonSerializer.Deserialize<GetLatestVersionsResponse>(content, JsonOptions);
			return result?.ModelVersions ?? new List<ModelVersion>();
		}

		public async Task<ModelVersion?> GetProductionModelVersionAsync(string modelName)
		{
			var versions = await GetLatestModelVersionsAsync(modelName, new[] { "Production" });
			return versions.FirstOrDefault();
		}

		public async Task<string> LogArtifactAsync(string runId, string localPath, string artifactPath = "model")
		{
			_logger.LogInformation("Uploading artifact for run {RunId}: {ArtifactPath} from {LocalPath}", runId, artifactPath, localPath);

			try
			{
				var run = await GetRunAsync(runId);
				var artifactUri = run?.Info?.ArtifactUri;

				if (string.IsNullOrEmpty(artifactUri))
				{
					_logger.LogWarning("Could not get artifact URI for run {RunId}, falling back to parameter logging", runId);
					await LogParamAsync(runId, "model_local_path", localPath);
					return localPath;
				}

				if (!File.Exists(localPath))
				{
					throw new FileNotFoundException($"Model file not found: {localPath}");
				}

				var fileBytes = await File.ReadAllBytesAsync(localPath);
				var fileName = Path.GetFileName(localPath);

				using var content = new MultipartFormDataContent();
				var fileContent = new ByteArrayContent(fileBytes);
				fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
				content.Add(fileContent, "file", fileName);

				var uploadUrl = $"/api/2.0/mlflow-artifacts/artifacts/{runId}/{artifactPath}/{fileName}";
				
				var response = await _httpClient.PutAsync(uploadUrl, new ByteArrayContent(fileBytes));

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogWarning("Primary artifact upload failed, trying alternative method");
					
					var artifactFullPath = $"{artifactUri}/{artifactPath}/{fileName}";
					await LogParamAsync(runId, "model_artifact_path", artifactFullPath);
					await LogParamAsync(runId, "model_local_path", localPath);
					
					_logger.LogInformation("Logged artifact path as parameter: {Path}", artifactFullPath);
					return localPath;
				}

				var resultPath = $"{artifactUri}/{artifactPath}/{fileName}";
				await LogParamAsync(runId, "model_artifact_uri", resultPath);
				await LogParamAsync(runId, "model_local_path", localPath);
				
				_logger.LogInformation("Successfully uploaded artifact to {Path}", resultPath);
				return resultPath;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to upload artifact for run {RunId}", runId);
				await LogParamAsync(runId, "model_local_path", localPath);
				return localPath;
			}
		}

		public async Task<string?> DownloadArtifactAsync(string runId, string artifactPath, string destinationPath)
		{
			_logger.LogInformation("Downloading artifact for run {RunId}: {ArtifactPath}", runId, artifactPath);

			try
			{
				var run = await GetRunAsync(runId);
				var localPathParam = run?.Data?.Params?.FirstOrDefault(p => p.Key == "model_local_path");
				
				if (localPathParam?.Value != null && File.Exists(localPathParam.Value))
				{
					_logger.LogInformation("Using local model file: {Path}", localPathParam.Value);
					return localPathParam.Value;
				}

				var artifactUri = run?.Info?.ArtifactUri;
				if (string.IsNullOrEmpty(artifactUri))
				{
					_logger.LogWarning("No artifact URI found for run {RunId}", runId);
					return null;
				}

				var downloadUrl = $"/api/2.0/mlflow-artifacts/artifacts/{runId}/{artifactPath}";
				var response = await _httpClient.GetAsync(downloadUrl);

				if (response.IsSuccessStatusCode)
				{
					var bytes = await response.Content.ReadAsByteArrayAsync();
					Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
					await File.WriteAllBytesAsync(destinationPath, bytes);
					_logger.LogInformation("Downloaded artifact to {Path}", destinationPath);
					return destinationPath;
				}

				_logger.LogWarning("Failed to download artifact: {StatusCode}", response.StatusCode);
				return localPathParam?.Value;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to download artifact for run {RunId}", runId);
				return null;
			}
		}

		public async Task<string?> GetModelArtifactPathAsync(string modelName, string? version = null)
		{
			try
			{
				ModelVersion? modelVersion;
				
				if (string.IsNullOrEmpty(version))
				{
					modelVersion = await GetProductionModelVersionAsync(modelName);
				}
				else
				{
					var versions = await GetLatestModelVersionsAsync(modelName);
					modelVersion = versions.FirstOrDefault(v => v.Version == version);
				}

				if (modelVersion?.RunId == null)
				{
					return null;
				}

				var run = await GetRunAsync(modelVersion.RunId);
				
				var localPath = run?.Data?.Params?.FirstOrDefault(p => p.Key == "model_local_path")?.Value;
				if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
				{
					return localPath;
				}

				var artifactUri = run?.Data?.Params?.FirstOrDefault(p => p.Key == "model_artifact_uri")?.Value;
				return artifactUri ?? localPath;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to get model artifact path for {ModelName}", modelName);
				return null;
			}
		}
	}

	public class CreateExperimentRequest
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;
	}

	public class CreateRunRequest
	{
		[JsonPropertyName("experiment_id")]
		public string ExperimentId { get; set; } = string.Empty;

		[JsonPropertyName("start_time")]
		public long StartTime { get; set; }

		[JsonPropertyName("tags")]
		public List<CreateRunTag>? Tags { get; set; }
	}

	public class CreateRunTag
	{
		[JsonPropertyName("key")]
		public string Key { get; set; } = string.Empty;

		[JsonPropertyName("value")]
		public string Value { get; set; } = string.Empty;
	}

	public class LogParamRequest
	{
		[JsonPropertyName("run_id")]
		public string RunId { get; set; } = string.Empty;

		[JsonPropertyName("key")]
		public string Key { get; set; } = string.Empty;

		[JsonPropertyName("value")]
		public string Value { get; set; } = string.Empty;
	}

	public class LogMetricRequest
	{
		[JsonPropertyName("run_id")]
		public string RunId { get; set; } = string.Empty;

		[JsonPropertyName("key")]
		public string Key { get; set; } = string.Empty;

		[JsonPropertyName("value")]
		public double Value { get; set; }

		[JsonPropertyName("timestamp")]
		public long Timestamp { get; set; }

		[JsonPropertyName("step")]
		public long Step { get; set; }
	}

	public class UpdateRunRequest
	{
		[JsonPropertyName("run_id")]
		public string RunId { get; set; } = string.Empty;

		[JsonPropertyName("status")]
		public string Status { get; set; } = string.Empty;

		[JsonPropertyName("end_time")]
		public long EndTime { get; set; }
	}

	public class SearchRunsRequest
	{
		[JsonPropertyName("experiment_ids")]
		public string[] ExperimentIds { get; set; } = Array.Empty<string>();

		[JsonPropertyName("filter_string")]
		public string FilterString { get; set; } = string.Empty;

		[JsonPropertyName("max_results")]
		public int MaxResults { get; set; }

		[JsonPropertyName("order_by")]
		public string[] OrderBy { get; set; } = Array.Empty<string>();
	}

	public class CreateRegisteredModelRequest
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		[JsonPropertyName("description")]
		public string? Description { get; set; }
	}

	public class CreateModelVersionRequest
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		[JsonPropertyName("source")]
		public string Source { get; set; } = string.Empty;

		[JsonPropertyName("run_id")]
		public string RunId { get; set; } = string.Empty;

		[JsonPropertyName("description")]
		public string? Description { get; set; }
	}

	public class TransitionStageRequest
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;

		[JsonPropertyName("version")]
		public string Version { get; set; } = string.Empty;

		[JsonPropertyName("stage")]
		public string Stage { get; set; } = string.Empty;

		[JsonPropertyName("archive_existing_versions")]
		public bool ArchiveExistingVersions { get; set; }
	}

	public class GetExperimentResponse
	{
		[JsonPropertyName("experiment")]
		public MLflowExperiment? Experiment { get; set; }
	}

	public class CreateExperimentResponse
	{
		[JsonPropertyName("experiment_id")]
		public string? ExperimentId { get; set; }
	}

	public class CreateRunResponse
	{
		[JsonPropertyName("run")]
		public MLflowRun? Run { get; set; }
	}

	public class GetRunResponse
	{
		[JsonPropertyName("run")]
		public MLflowRun? Run { get; set; }
	}

	public class SearchRunsResponse
	{
		[JsonPropertyName("runs")]
		public List<MLflowRun>? Runs { get; set; }
	}

	public class CreateRegisteredModelResponse
	{
		[JsonPropertyName("registered_model")]
		public RegisteredModel? RegisteredModel { get; set; }
	}

	public class GetRegisteredModelResponse
	{
		[JsonPropertyName("registered_model")]
		public RegisteredModel? RegisteredModel { get; set; }
	}

	public class CreateModelVersionResponse
	{
		[JsonPropertyName("model_version")]
		public ModelVersion? ModelVersion { get; set; }
	}

	public class GetLatestVersionsResponse
	{
		[JsonPropertyName("model_versions")]
		public List<ModelVersion>? ModelVersions { get; set; }
	}

	public class MLflowExperiment
	{
		[JsonPropertyName("experiment_id")]
		public string? ExperimentId { get; set; }

		[JsonPropertyName("name")]
		public string? Name { get; set; }

		[JsonPropertyName("artifact_location")]
		public string? ArtifactLocation { get; set; }

		[JsonPropertyName("lifecycle_stage")]
		public string? LifecycleStage { get; set; }
	}

	public class MLflowRun
	{
		[JsonPropertyName("info")]
		public RunInfo? Info { get; set; }

		[JsonPropertyName("data")]
		public RunData? Data { get; set; }
	}

	public class RunInfo
	{
		[JsonPropertyName("run_id")]
		public string? RunId { get; set; }

		[JsonPropertyName("experiment_id")]
		public string? ExperimentId { get; set; }

		[JsonPropertyName("status")]
		public string? Status { get; set; }

		[JsonPropertyName("start_time")]
		public long? StartTime { get; set; }

		[JsonPropertyName("end_time")]
		public long? EndTime { get; set; }

		[JsonPropertyName("artifact_uri")]
		public string? ArtifactUri { get; set; }
	}

	public class RunData
	{
		[JsonPropertyName("metrics")]
		public List<RunMetric>? Metrics { get; set; }

		[JsonPropertyName("params")]
		public List<RunParam>? Params { get; set; }

		[JsonPropertyName("tags")]
		public List<RunTag>? Tags { get; set; }
	}

	public class RunMetric
	{
		[JsonPropertyName("key")]
		public string? Key { get; set; }

		[JsonPropertyName("value")]
		public double Value { get; set; }

		[JsonPropertyName("timestamp")]
		public long? Timestamp { get; set; }

		[JsonPropertyName("step")]
		public long? Step { get; set; }
	}

	public class RunParam
	{
		[JsonPropertyName("key")]
		public string? Key { get; set; }

		[JsonPropertyName("value")]
		public string? Value { get; set; }
	}

	public class RunTag
	{
		[JsonPropertyName("key")]
		public string? Key { get; set; }

		[JsonPropertyName("value")]
		public string? Value { get; set; }
	}

	public class RegisteredModel
	{
		[JsonPropertyName("name")]
		public string? Name { get; set; }

		[JsonPropertyName("creation_timestamp")]
		public long? CreationTimestamp { get; set; }

		[JsonPropertyName("last_updated_timestamp")]
		public long? LastUpdatedTimestamp { get; set; }

		[JsonPropertyName("description")]
		public string? Description { get; set; }

		[JsonPropertyName("latest_versions")]
		public List<ModelVersion>? LatestVersions { get; set; }
	}

	public class ModelVersion
	{
		[JsonPropertyName("name")]
		public string? Name { get; set; }

		[JsonPropertyName("version")]
		public string? Version { get; set; }

		[JsonPropertyName("creation_timestamp")]
		public long? CreationTimestamp { get; set; }

		[JsonPropertyName("current_stage")]
		public string? CurrentStage { get; set; }

		[JsonPropertyName("description")]
		public string? Description { get; set; }

		[JsonPropertyName("source")]
		public string? Source { get; set; }

		[JsonPropertyName("run_id")]
		public string? RunId { get; set; }

		[JsonPropertyName("status")]
		public string? Status { get; set; }
	}
}
