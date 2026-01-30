using Microsoft.AspNetCore.Mvc;
using StudentService.Domain.Repositories;

namespace StudentService.API.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class CompaniesController : ControllerBase
	{
		private readonly ICompanyRepository _companyRepository;

		public CompaniesController(ICompanyRepository companyRepository)
		{
			_companyRepository = companyRepository;
		}

		[HttpGet]
		public async Task<IActionResult> GetAll()
		{
			var result = await _companyRepository.GetAllAsync();
			return Ok(result);
		}

		[HttpGet("{id}")]
		public async Task<IActionResult> GetById(string id)
		{
			var result = await _companyRepository.GetByIdAsync(id);
			return Ok(result);
		}

		[HttpGet("search")]
		public async Task<IActionResult> GetByName([FromQuery] string name)
		{
			var result = await _companyRepository.GetByNameAsync(name);
			return Ok(result);
		}
	}
}
