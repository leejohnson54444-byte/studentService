using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StudentService.Infrastructure.Data;

namespace StudentService.Infrastructure.Services
{
	public class DataSeederBackgroundService : BackgroundService
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly ILogger<DataSeederBackgroundService> _logger;
		private readonly TimeSpan _scheduledTime;
		private readonly bool _enabled;
		private readonly int _maxRetries;
		private readonly TimeSpan _initialRetryDelay;

		public DataSeederBackgroundService(
			IServiceProvider serviceProvider,
			ILogger<DataSeederBackgroundService> logger,
			TimeSpan? scheduledTime = null,
			bool enabled = true,
			int maxRetries = 10,
			TimeSpan? initialRetryDelay = null)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
			_scheduledTime = scheduledTime ?? new TimeSpan(21, 0, 0);
			_enabled = enabled;
			_maxRetries = maxRetries;
			_initialRetryDelay = initialRetryDelay ?? TimeSpan.FromSeconds(30);
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_enabled)
			{
				_logger.LogInformation("Automatic data seeding is disabled");
				return;
			}

			_logger.LogInformation("Data seeder background service started. Scheduled time: {ScheduledTime}", _scheduledTime);

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					var delay = CalculateDelayUntilNextRun();
					_logger.LogInformation("Next data seeding scheduled in {Delay} at {NextRunTime}",
						delay, DateTime.Now.Add(delay));

					await Task.Delay(delay, stoppingToken);

					if (!stoppingToken.IsCancellationRequested)
					{
						await RunDataSeederWithRetryAsync(stoppingToken);
					}
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Unexpected error in data seeder background service");
					await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
				}
			}
		}

		private TimeSpan CalculateDelayUntilNextRun()
		{
			var now = DateTime.Now;
			var scheduledToday = now.Date.Add(_scheduledTime);

			if (now >= scheduledToday)
			{
				scheduledToday = scheduledToday.AddDays(1);
			}

			return scheduledToday - now;
		}

		private async Task RunDataSeederWithRetryAsync(CancellationToken cancellationToken)
		{
			int attempt = 0;
			var currentDelay = _initialRetryDelay;

			while (!cancellationToken.IsCancellationRequested)
			{
				attempt++;
				_logger.LogInformation("Starting data seeder (attempt {Attempt})", attempt);

				try
				{
					using var scope = _serviceProvider.CreateScope();
					var dataSeeder = scope.ServiceProvider.GetRequiredService<DataSeeder>();

					await dataSeeder.DailySeedDataAsync();

					_logger.LogInformation("Data seeding completed successfully at {Time}", DateTime.UtcNow);
					return;
				}
				catch (HttpRequestException ex) when (IsRetryableStatusCode(ex))
				{
					_logger.LogWarning(ex, 
						"Data seeder failed with retryable error on attempt {Attempt}. Retrying in {Delay}...",
						attempt, currentDelay);

					if (attempt >= _maxRetries)
					{
						_logger.LogError("Data seeder failed after {MaxRetries} attempts. Giving up until next scheduled run.", _maxRetries);
						return;
					}

					await Task.Delay(currentDelay, cancellationToken);
					
					currentDelay = TimeSpan.FromTicks(Math.Min(
						currentDelay.Ticks * 2,
						TimeSpan.FromMinutes(10).Ticks));
				}
				catch (Exception ex) when (IsRetryableException(ex))
				{
					_logger.LogWarning(ex,
						"Data seeder failed with retryable error on attempt {Attempt}. Retrying in {Delay}...",
						attempt, currentDelay);

					if (attempt >= _maxRetries)
					{
						_logger.LogError("Data seeder failed after {MaxRetries} attempts. Giving up until next scheduled run.", _maxRetries);
						return;
					}

					await Task.Delay(currentDelay, cancellationToken);

					currentDelay = TimeSpan.FromTicks(Math.Min(
						currentDelay.Ticks * 2,
						TimeSpan.FromMinutes(10).Ticks));
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Data seeder failed with non-retryable error on attempt {Attempt}", attempt);
					return;
				}
			}
		}

		private static bool IsRetryableStatusCode(HttpRequestException ex)
		{
			if (ex.StatusCode.HasValue)
			{
				var statusCode = (int)ex.StatusCode.Value;
				return statusCode == 500 || statusCode == 503;
			}

			var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
			return message.Contains("500") || message.Contains("503") ||
				   message.Contains("internal server error") ||
				   message.Contains("service unavailable");
		}

		private static bool IsRetryableException(Exception ex)
		{
			if (ex.InnerException is HttpRequestException httpEx)
			{
				return IsRetryableStatusCode(httpEx);
			}

			var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
			return message.Contains("500") || 
				   message.Contains("503") ||
				   message.Contains("internal server error") ||
				   message.Contains("service unavailable") ||
				   message.Contains("temporarily unavailable");
		}

		public async Task TriggerDataSeederAsync(CancellationToken cancellationToken = default)
		{
			await RunDataSeederWithRetryAsync(cancellationToken);
		}
	}
}
