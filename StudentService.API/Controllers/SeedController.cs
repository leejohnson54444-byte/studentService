using Microsoft.AspNetCore.Mvc;
using StudentService.Infrastructure.Data;

namespace StudentService.API.Controllers
{
	/// <summary>
	/// Controller for managing data seeding operations.
	/// Used to initialize the database with sample data for demos.
	/// </summary>
	[ApiController]
	[Route("api/[controller]")]
	public class SeedController : ControllerBase
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly ILogger<SeedController> _logger;

		public SeedController(IServiceProvider serviceProvider, ILogger<SeedController> logger)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
		}

		/// <summary>
		/// Seeds the database with initial demo data.
		/// This includes companies, jobs, students, and applications.
		/// Warning: This operation may take several minutes to complete.
		/// </summary>
		/// <returns>Status of the seeding operation</returns>
		[HttpPost("initial")]
		[ProducesResponseType(StatusCodes.Status200OK)]
		[ProducesResponseType(StatusCodes.Status500InternalServerError)]
		public async Task<IActionResult> SeedInitialData()
		{
			try
			{
				_logger.LogInformation("Starting initial data seeding via API...");

				using var scope = _serviceProvider.CreateScope();
				var dataSeeder = scope.ServiceProvider.GetRequiredService<DataSeeder>();

				await dataSeeder.OneTimeSeedDataAsync();

				_logger.LogInformation("Initial data seeding completed successfully");
				return Ok(new { message = "Initial data seeding completed successfully", timestamp = DateTime.UtcNow });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error during initial data seeding");
				return StatusCode(500, new { error = "Data seeding failed", details = ex.Message });
			}
		}

		/// <summary>
		/// Seeds daily incremental data (new students, applications, etc).
		/// This is the same operation that runs automatically at 9 PM.
		/// </summary>
		/// <returns>Status of the seeding operation</returns>
		[HttpPost("daily")]
		[ProducesResponseType(StatusCodes.Status200OK)]
		[ProducesResponseType(StatusCodes.Status500InternalServerError)]
		public async Task<IActionResult> SeedDailyData()
		{
			try
			{
				_logger.LogInformation("Starting daily data seeding via API...");

				using var scope = _serviceProvider.CreateScope();
				var dataSeeder = scope.ServiceProvider.GetRequiredService<DataSeeder>();

				await dataSeeder.DailySeedDataAsync();

				_logger.LogInformation("Daily data seeding completed successfully");
				return Ok(new { message = "Daily data seeding completed successfully", timestamp = DateTime.UtcNow });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error during daily data seeding");
				return StatusCode(500, new { error = "Data seeding failed", details = ex.Message });
			}
		}

		/// <summary>
		/// Checks if the database has been seeded with initial data.
		/// </summary>
		/// <returns>Status of whether data exists</returns>
		[HttpGet("status")]
		[ProducesResponseType(StatusCodes.Status200OK)]
		public async Task<IActionResult> GetSeedStatus()
		{
			try
			{
				using var scope = _serviceProvider.CreateScope();
				var mongoContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

				var jobsCollection = mongoContext.GetCollection<Infrastructure.Models.Job>("jobs");
				var studentsCollection = mongoContext.GetCollection<Infrastructure.Models.Student>("students");
				var companiesCollection = mongoContext.GetCollection<Infrastructure.Models.Company>("companies");
				var applicationsCollection = mongoContext.GetCollection<Infrastructure.Models.Application>("applications");

				var jobCount = await jobsCollection.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<Infrastructure.Models.Job>.Empty);
				var studentCount = await studentsCollection.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<Infrastructure.Models.Student>.Empty);
				var companyCount = await companiesCollection.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<Infrastructure.Models.Company>.Empty);
				var applicationCount = await applicationsCollection.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<Infrastructure.Models.Application>.Empty);

				var isSeeded = jobCount > 0 || studentCount > 0 || companyCount > 0;

				return Ok(new
				{
					isSeeded,
					counts = new
					{
						jobs = jobCount,
						students = studentCount,
						companies = companyCount,
						applications = applicationCount
					},
					timestamp = DateTime.UtcNow
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error checking seed status");
				return StatusCode(500, new { error = "Failed to check seed status", details = ex.Message });
			}
		}
	}
}
