using Microsoft.AspNetCore.Mvc;
using StudentService.Infrastructure.Data;
using System.Threading.Tasks;

namespace StudentService.API.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class DataSeederController : ControllerBase
	{
		private readonly DataSeeder _dataSeeder;

		public DataSeederController(DataSeeder dataSeeder)
		{
			_dataSeeder = dataSeeder;
		}

		[HttpGet]
		public async Task<IActionResult> SeedDataAsync()
        {
			await _dataSeeder.DailySeedDataAsync();
			return Ok(new { message = "Data generation completed successfully" });
		}
	}
}