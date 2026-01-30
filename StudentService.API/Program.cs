using Microsoft.SemanticKernel;
using StudentService.Domain.Repositories;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Repositories;
using StudentService.Domain.Services;
using StudentService.Infrastructure.Services;
using StudentService.Infrastructure.MLflow;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly;
using Polly.Extensions.Http;

namespace StudentService.API
{
	public class Program
	{
		public static async Task Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);

			ConfigureServices(builder);

			var app = builder.Build();

			ConfigurePipeline(app);

			await InitializeSearchIndex(app);

			app.Run();
		}

		private static void ConfigureServices(WebApplicationBuilder builder)
		{
			var config = builder.Configuration;

			// Semantic Kernel
			builder.Services.AddSingleton<Kernel>(_ =>
			{
				var kb = Kernel.CreateBuilder();
				kb.AddOpenAIChatCompletion(
					modelId: config["OpenAI:ModelId"] ?? "gemma-3-27b-it",
					endpoint: new Uri(config["OpenAI:Endpoint"] ?? "https://generativelanguage.googleapis.com/v1beta/openai/"),
					apiKey: config["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey configuration is required")
				);
				return kb.Build();
			});

			builder.Services.AddScoped<ICompanyRepository, CompanyRepository>();
			builder.Services.AddScoped<IJobRepository, JobRepository>();
			builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
			builder.Services.AddScoped<IStudentRepository, StudentRepository>();

			builder.Services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());

			builder.Services.AddControllers();

			// MongoDB connection - prioritize environment variable (MongoDB__ConnectionString) over appsettings
			var mongoConnectionString = config["MongoDB:ConnectionString"] 
				?? config.GetConnectionString("MongoDB") 
				?? "mongodb://localhost:27017";
			var mongoDatabaseName = config["MongoDB:DatabaseName"] ?? "student_service_DB";
			
			var mongoContext = new MongoDbContext(mongoConnectionString, mongoDatabaseName);
			builder.Services.AddSingleton(mongoContext);

			builder.Services.AddScoped<DataScraper>();
			builder.Services.AddHttpClient("Default")
				.AddPolicyHandler(GetRetryPolicy())
				.AddPolicyHandler(GetCircuitBreakerPolicy());
			builder.Services.AddHttpClient();
			builder.Services.AddScoped<DataSeeder>();

			ConfigureElasticsearch(builder);

			ConfigureMLflow(builder, mongoContext);

			builder.Services.AddScoped<IJobRecommendationService>(sp =>
			{
				var context = sp.GetRequiredService<MongoDbContext>();
				var mapper = sp.GetRequiredService<AutoMapper.IMapper>();
				var trainingService = sp.GetRequiredService<ModelTrainingService>();
				return new JobRecommendationService(context, mapper, trainingService);
			});
			
			builder.Services.AddScoped<IStudentRecommendationService>(sp =>
			{
				var context = sp.GetRequiredService<MongoDbContext>();
				var mapper = sp.GetRequiredService<AutoMapper.IMapper>();
				var trainingService = sp.GetRequiredService<ModelTrainingService>();
				return new StudentRecommendationService(context, mapper, trainingService);
			});
			
			builder.Services.AddScoped<IStudentApplicationService, StudentApplicationService>();
			builder.Services.AddScoped<IApplicationService, ApplicationService>();
			builder.Services.AddScoped<IJobService, JobService>();
			builder.Services.AddScoped<IJobPayPredictionService, JobPayPredictionService>();
			
			builder.Services.AddScoped<IJobSearchService>(sp =>
			{
				var elasticClient = sp.GetRequiredService<ElasticsearchClient>();
				var mongoContext = sp.GetRequiredService<MongoDbContext>();
				var logger = sp.GetRequiredService<ILogger<JobSearchService>>();
				var indexName = builder.Configuration["Elasticsearch:IndexName"] ?? "jobs";
				return new JobSearchService(elasticClient, mongoContext, logger, indexName);
			});

			ConfigureDataSeederBackgroundService(builder);

			ConfigureHealthChecks(builder, mongoContext);

			builder.Services.AddEndpointsApiExplorer();
			builder.Services.AddSwaggerGen();

			builder.Services.AddCors(options =>
			{
				options.AddPolicy("Blazor", policy =>
				{
					policy.WithOrigins(
						// Development URLs
						"https://localhost:7025", 
						"http://localhost:7025",
						"https://localhost:5078",
						"http://localhost:5078",
						"https://localhost:44389",
						"http://localhost:2913",
						// Docker/Production URLs
						"https://localhost:5001",
						"http://localhost:5001",
						"http://localhost:80", 
						"http://localhost",
						"http://webapp:8080"
					)
						.AllowAnyMethod()
						.AllowAnyHeader()
						.AllowCredentials();
				});
			});
		}

		private static void ConfigureElasticsearch(WebApplicationBuilder builder)
		{
			var config = builder.Configuration;
			var esUri = config["Elasticsearch:Uri"];
			var esApiKey = config["Elasticsearch:ApiKey"];
			var esIndexName = config["Elasticsearch:IndexName"] ?? "jobs";

			ElasticsearchClientSettings settings;

			if (!string.IsNullOrWhiteSpace(esUri) && !string.IsNullOrWhiteSpace(esApiKey))
			{
				var uri = new Uri(esUri);
				
				settings = new ElasticsearchClientSettings(uri)
					.DefaultIndex(esIndexName)
					.Authentication(new ApiKey(esApiKey))
					.RequestTimeout(TimeSpan.FromSeconds(60))
					.DisableDirectStreaming()
					.ServerCertificateValidationCallback((sender, cert, chain, errors) => true);
			}
			else if (!string.IsNullOrWhiteSpace(esUri))
			{
				var uri = new Uri(esUri);
				settings = new ElasticsearchClientSettings(uri)
					.DefaultIndex(esIndexName)
					.RequestTimeout(TimeSpan.FromSeconds(30))
					.DisableDirectStreaming();
			}
			else
			{
				settings = new ElasticsearchClientSettings(new Uri("http://localhost:9200"))
					.DefaultIndex(esIndexName)
					.RequestTimeout(TimeSpan.FromSeconds(30))
					.DisableDirectStreaming();
			}

			var client = new ElasticsearchClient(settings);
			builder.Services.AddSingleton(client);
		}

		private static void ConfigureMLflow(WebApplicationBuilder builder, MongoDbContext mongoContext)
		{
			var config = builder.Configuration;
			var mlflowUri = config["MLflow:TrackingUri"] ?? "http://localhost:5000";
			var modelStoragePath = config["MLflow:ModelStoragePath"] ?? "models";
			var autoTrainingEnabled = config.GetValue("MLflow:AutoTrainingEnabled", true);
			
			var trainingTimeStr = config["MLflow:TrainingScheduledTime"] ?? "22:00";
			var trainingScheduledTime = TimeSpan.TryParse(trainingTimeStr, out var parsedTime) 
				? parsedTime 
				: new TimeSpan(22, 0, 0);

			builder.Services.AddHttpClient<MLflowClient>((serviceProvider, httpClient) =>
			{
				httpClient.BaseAddress = new Uri(mlflowUri);
				httpClient.Timeout = TimeSpan.FromMinutes(5);
			});

			builder.Services.AddSingleton(sp =>
			{
				var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
				var logger = sp.GetRequiredService<ILogger<MLflowClient>>();
				var httpClient = httpClientFactory.CreateClient(nameof(MLflowClient));
				return new MLflowClient(httpClient, logger, mlflowUri);
			});

			builder.Services.AddSingleton(sp =>
			{
				var mlflowClient = sp.GetRequiredService<MLflowClient>();
				var logger = sp.GetRequiredService<ILogger<ModelTrainingService>>();
				return new ModelTrainingService(mlflowClient, mongoContext, logger, modelStoragePath);
			});

			builder.Services.AddSingleton(sp =>
			{
				var logger = sp.GetRequiredService<ILogger<AutomaticModelTrainingService>>();
				return new AutomaticModelTrainingService(
					sp,
					logger,
					trainingScheduledTime,
					autoTrainingEnabled);
			});
			builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomaticModelTrainingService>());

			builder.Services.AddScoped<IModelManagementService>(sp =>
			{
				var trainingService = sp.GetRequiredService<ModelTrainingService>();
				var autoTrainingService = sp.GetRequiredService<AutomaticModelTrainingService>();
				var mlflowClient = sp.GetRequiredService<MLflowClient>();
				var logger = sp.GetRequiredService<ILogger<ModelManagementService>>();
				return new ModelManagementService(trainingService, autoTrainingService, mlflowClient, logger);
			});
		}

		private static void ConfigureDataSeederBackgroundService(WebApplicationBuilder builder)
		{
			var config = builder.Configuration;
			var dataSeederEnabled = config.GetValue("DataSeeder:AutoSeederEnabled", true);
			
			var seederTimeStr = config["DataSeeder:ScheduledTime"] ?? "21:00";
			var seederScheduledTime = TimeSpan.TryParse(seederTimeStr, out var parsedTime) 
				? parsedTime 
				: new TimeSpan(21, 0, 0);

			var maxRetries = config.GetValue("DataSeeder:MaxRetries", 10);
			var initialRetryDelaySeconds = config.GetValue("DataSeeder:InitialRetryDelaySeconds", 30);

			builder.Services.AddSingleton(sp =>
			{
				var logger = sp.GetRequiredService<ILogger<DataSeederBackgroundService>>();
				return new DataSeederBackgroundService(
					sp,
					logger,
					seederScheduledTime,
					dataSeederEnabled,
					maxRetries,
					TimeSpan.FromSeconds(initialRetryDelaySeconds));
			});
			builder.Services.AddHostedService(sp => sp.GetRequiredService<DataSeederBackgroundService>());
		}

		private static void ConfigureHealthChecks(WebApplicationBuilder builder, MongoDbContext mongoContext)
		{
			var config = builder.Configuration;
			
			builder.Services.AddHealthChecks()
				.AddCheck("mongodb", () =>
				{
					try
					{
						var isHealthy = mongoContext.IsHealthyAsync().GetAwaiter().GetResult();
						return isHealthy 
							? HealthCheckResult.Healthy("MongoDB is responsive")
							: HealthCheckResult.Unhealthy("MongoDB is not responsive");
					}
					catch (Exception ex)
					{
						return HealthCheckResult.Unhealthy($"MongoDB health check failed: {ex.Message}");
					}
				}, tags: new[] { "db", "mongodb" })
				.AddCheck("self", () => HealthCheckResult.Healthy("API is running"), tags: new[] { "api" });
		}

		private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
		{
			return HttpPolicyExtensions
				.HandleTransientHttpError()
				.OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
				.WaitAndRetryAsync(3, retryAttempt => 
					TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
		}

		private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
		{
			return HttpPolicyExtensions
				.HandleTransientHttpError()
				.CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
		}

		private static void ConfigurePipeline(WebApplication app)
		{
			app.UseSwagger();
			app.UseSwaggerUI();

			app.UseHttpsRedirection();
			app.UseCors("Blazor");
			
			app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
			{
				Predicate = _ => true,
				ResponseWriter = async (context, report) =>
				{
					context.Response.ContentType = "application/json";
					var result = new
					{
						status = report.Status.ToString(),
						checks = report.Entries.Select(entry => new
						{
							name = entry.Key,
							status = entry.Value.Status.ToString(),
							description = entry.Value.Description,
							duration = entry.Value.Duration.TotalMilliseconds
						}),
						totalDuration = report.TotalDuration.TotalMilliseconds
					};
					await context.Response.WriteAsJsonAsync(result);
				}
			});
			
			app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
			{
				Predicate = check => check.Tags.Contains("db")
			});
			
			app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
			{
				Predicate = check => check.Tags.Contains("api")
			});

			app.UseAuthorization();
			app.MapControllers();
		}

		private static async Task InitializeSearchIndex(WebApplication app)
		{
			using var scope = app.Services.CreateScope();
			var search = scope.ServiceProvider.GetRequiredService<IJobSearchService>();
			await search.ReindexAllAsync();
		}
	}
}
