using AutoMapper;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentService.Domain.DTOs;
using StudentService.Domain.Repositories;
using StudentService.Infrastructure.Data;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Repositories
{
	public class CompanyRepository : ICompanyRepository
	{
		private readonly MongoDbContext _context;
		private readonly IMapper _mapper;

		public CompanyRepository(MongoDbContext context, IMapper mapper)
		{
			_context = context;
			_mapper = mapper;
		}

		public async Task<IEnumerable<CompanyInfo>> GetAllAsync()
		{
			var companies = await _context.GetCollection<Company>("companies")
				.Find(_ => true)
				.ToListAsync();

			return companies.Select(c => _mapper.Map<CompanyInfo>(c));
		}

		public async Task<CompanyInfo> GetByIdAsync(string id)
		{
			var oid = ParseObjectId(id, nameof(id));

			var company = await _context.GetCollection<Company>("companies")
				.Find(x => x.CompanyId == oid)
				.SingleOrDefaultAsync()
				?? throw new KeyNotFoundException($"Company '{oid}' not found");

			return _mapper.Map<CompanyInfo>(company);
		}

		public async Task<IEnumerable<CompanyInfo>> GetByNameAsync(string name)
		{
			var companies = await _context.GetCollection<Company>("companies")
				.Find(x => x.Name.ToLower().Contains(name.ToLower()))
				.ToListAsync();

			return companies.Select(c => _mapper.Map<CompanyInfo>(c));
		}

		private static ObjectId ParseObjectId(string id, string paramName)
		{
			if (!ObjectId.TryParse(id, out var oid))
				throw new ArgumentException($"Invalid {paramName}", paramName);
			return oid;
		}
	}
}
