using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Transport;
using StudentService.Domain.DTOs;
using StudentService.Domain.Services;
using Microsoft.Extensions.Logging;
using StudentService.Infrastructure.Data;
using MongoDB.Driver;
using JobModel = StudentService.Infrastructure.Models.Job;

namespace StudentService.Infrastructure.Services
{
	public class JobSearchService : IJobSearchService
	{
		private readonly ElasticsearchClient _client;
		private readonly MongoDbContext _mongoContext;
		private readonly ILogger<JobSearchService> _logger;
		private readonly string _indexName;
		private bool _elasticsearchAvailable = true;

		public JobSearchService(ElasticsearchClient client, MongoDbContext mongoContext, ILogger<JobSearchService> logger, string indexName = "jobs")
		{
			_client = client;
			_mongoContext = mongoContext;
			_logger = logger;
			_indexName = indexName;
		}

		public async Task<IEnumerable<JobBasicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20)
		{
			if (page < 1) page = 1;
			if (pageSize <= 0) pageSize = 20;

			if (!_elasticsearchAvailable)
			{
				_logger.LogWarning("Elasticsearch unavailable, falling back to MongoDB search");
				return await SearchMongoDbAsync(query, page, pageSize);
			}

			try
			{
				if (string.IsNullOrWhiteSpace(query))
					return await GetAllJobsFromElasticsearchAsync(page, pageSize);

				return await SearchElasticsearchAsync(query, page, pageSize);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Elasticsearch search failed, falling back to MongoDB");
				return await SearchMongoDbAsync(query, page, pageSize);
			}
		}

		private async Task<IEnumerable<JobBasicInfo>> SearchMongoDbAsync(string query, int page, int pageSize)
		{
			_logger.LogInformation("Searching MongoDB for query: {Query}", query);

			var collection = _mongoContext.GetCollection<JobModel>("jobs");
			
			FilterDefinition<JobModel> filter;
			
			if (string.IsNullOrWhiteSpace(query))
			{
				filter = Builders<JobModel>.Filter.Empty;
			}
			else
			{
				var titleFilter = Builders<JobModel>.Filter.Regex(j => j.Title, new MongoDB.Bson.BsonRegularExpression(query, "i"));
				var descFilter = Builders<JobModel>.Filter.Regex(j => j.Description, new MongoDB.Bson.BsonRegularExpression(query, "i"));
				filter = Builders<JobModel>.Filter.Or(titleFilter, descFilter);
			}

			var jobs = await collection
				.Find(filter)
				.SortByDescending(j => j.StartDate)
				.Skip((page - 1) * pageSize)
				.Limit(pageSize)
				.ToListAsync();

			_logger.LogInformation("MongoDB returned {Count} jobs", jobs.Count);

			return jobs.Select(j => new JobBasicInfo
			{
				Id = j.JobId.ToString(),
				Title = j.Title,
				StartDate = j.StartDate,
				EndDate = j.EndDate,
				PlaceOfWork = j.PlaceOfWork,
				Description = j.Description
			});
		}

		public async Task ReindexAllAsync()
		{
			_logger.LogInformation("Starting Elasticsearch reindex from MongoDB...");

			try
			{
				var jobs = await _mongoContext.GetCollection<JobModel>("jobs")
					.Find(_ => true)
					.ToListAsync();

				_logger.LogInformation("Found {Count} jobs in MongoDB to index", jobs.Count);

				if (jobs.Count == 0)
				{
					_logger.LogWarning("No jobs to index");
					return;
				}

				await BulkIndexJobsAsync(jobs);
				_elasticsearchAvailable = true;
				_logger.LogInformation("Elasticsearch indexing completed successfully");
			}
			catch (TransportException tex)
			{
				_logger.LogWarning(tex, "Elasticsearch transport error. Search will use MongoDB fallback.");
				_logger.LogWarning("Status code: {StatusCode}", tex.Message);
				_elasticsearchAvailable = false;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to index to Elasticsearch. Search will use MongoDB fallback.");
				_elasticsearchAvailable = false;
			}
		}

		private async Task<IEnumerable<JobBasicInfo>> GetAllJobsFromElasticsearchAsync(int page, int pageSize)
		{
			_logger.LogInformation("Fetching all jobs from Elasticsearch (page {Page}, size {PageSize})", page, pageSize);

			var searchResponse = await _client.SearchAsync<JobDocument>(s => s
				.Index(_indexName)
				.Query(q => q.MatchAll(_ => {}))
				.From((page - 1) * pageSize)
				.Size(pageSize)
				.Sort(so => so.Field("startDate", f => f.Order(SortOrder.Desc)))
			);

			if (!searchResponse.IsValidResponse)
			{
				_logger.LogError("Elasticsearch fetch failed: {Error}", searchResponse.ElasticsearchServerError?.Error?.Reason ?? "Unknown error");
				_logger.LogError("Debug info: {DebugInfo}", searchResponse.DebugInformation);
				
				_elasticsearchAvailable = false;
				return await SearchMongoDbAsync(string.Empty, page, pageSize);
			}

			_logger.LogInformation("Elasticsearch returned {Count} jobs", searchResponse.Hits.Count);

			return searchResponse.Hits.Select(h => new JobBasicInfo
			{
				Id = h.Id!,
				Title = h.Source?.Title ?? string.Empty,
				StartDate = h.Source?.StartDate ?? DateTime.MinValue,
				EndDate = h.Source?.EndDate ?? DateTime.MinValue,
				PlaceOfWork = h.Source?.PlaceOfWork ?? string.Empty,
				Description = h.Source?.Description
			});
		}

		private async Task<IEnumerable<JobBasicInfo>> SearchElasticsearchAsync(string query, int page, int pageSize)
		{
			_logger.LogInformation("Searching Elasticsearch for query: {Query}", query);

			var searchResponse = await _client.SearchAsync<JobDocument>(s => s
				.Index(_indexName)
				.Query(q => q
					.Bool(b => b
						.Should(
							sh => sh.MatchPhrase(mp => mp
								.Field("title")
								.Query(query)
								.Boost(4)
							),
							sh => sh.Match(m => m
								.Field("title")
								.Query(query)
								.Fuzziness(new Fuzziness("AUTO"))
								.Boost(2)
							),
							sh => sh.MatchPhrase(mp => mp
								.Field("description")
								.Query(query)
								.Boost(1.5f)
							),
							sh => sh.Match(m => m
								.Field("description")
								.Query(query)
								.Fuzziness(new Fuzziness("AUTO"))
								.Boost(1)
							)
						)
						.MinimumShouldMatch(1)
					)
				)
				.From((page - 1) * pageSize)
				.Size(pageSize)
			);

			if (!searchResponse.IsValidResponse)
			{
				_logger.LogError("Elasticsearch search failed: {Error}", searchResponse.ElasticsearchServerError?.Error?.Reason ?? "Unknown error");
				_logger.LogError("Debug info: {DebugInfo}", searchResponse.DebugInformation);
				
				_elasticsearchAvailable = false;
				return await SearchMongoDbAsync(query, page, pageSize);
			}

			_logger.LogInformation("Elasticsearch returned {Count} results", searchResponse.Hits.Count);

			return searchResponse.Hits.Select(h => new JobBasicInfo
			{
				Id = h.Id!,
				Title = h.Source?.Title ?? string.Empty,
				StartDate = h.Source?.StartDate ?? DateTime.MinValue,
				EndDate = h.Source?.EndDate ?? DateTime.MinValue,
				PlaceOfWork = h.Source?.PlaceOfWork ?? string.Empty,
				Description = h.Source?.Description
			});
		}

		private async Task BulkIndexJobsAsync(List<JobModel> jobs)
		{
			_logger.LogInformation("Bulk indexing {Count} jobs to index '{IndexName}'...", jobs.Count, _indexName);

			var documents = jobs.Select(j => new JobDocument
			{
				Id = j.JobId.ToString(),
				Title = j.Title,
				Description = j.Description,
				Type = j.Type,
				HourlyPay = j.HourlyPay,
				StartDate = j.StartDate,
				EndDate = j.EndDate,
				PlaceOfWork = j.PlaceOfWork,
				RequiredTraits = j.RequiredTraits
			}).ToList();

			var bulkResponse = await _client.BulkAsync(b => b
				.Index(_indexName)
				.IndexMany(documents, (descriptor, doc) => descriptor.Id(doc.Id))
			);

			if (!bulkResponse.IsValidResponse)
			{
				_logger.LogError("Bulk indexing failed: {Error}", bulkResponse.ElasticsearchServerError?.Error?.Reason ?? "Unknown error");
				_logger.LogError("Debug info: {DebugInfo}", bulkResponse.DebugInformation);
				throw new Exception($"Bulk indexing failed: {bulkResponse.DebugInformation}");
			}
			else if (bulkResponse.Errors)
			{
				var errorItems = bulkResponse.ItemsWithErrors.ToList();
				_logger.LogError("Bulk indexing had {Count} errors", errorItems.Count);
				foreach (var item in errorItems.Take(5))
				{
					_logger.LogError("Error indexing document {Id}: {Error}", item.Id, item.Error?.Reason);
				}
				throw new Exception($"Bulk indexing had {errorItems.Count} errors");
			}
			else
			{
				_logger.LogInformation("Successfully indexed {Count} jobs to Elasticsearch", documents.Count);
			}
		}
	}

	public class JobDocument
	{
		public string Id { get; set; } = string.Empty;
		public string Title { get; set; } = string.Empty;
		public string? Description { get; set; }
		public string Type { get; set; } = string.Empty;
		public double HourlyPay { get; set; }
		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public string PlaceOfWork { get; set; } = string.Empty;
		public List<string>? RequiredTraits { get; set; }
	}
}
