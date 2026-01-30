using System.Text;
using MongoDB.Bson;
using StudentService.Infrastructure.Models;
using HtmlAgilityPack;
using MongoDB.Driver;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Linq;

namespace StudentService.Infrastructure.Data
{
	public class DataScraper
	{
		private readonly IMongoCollection<Job> _job;
		private readonly IMongoCollection<Company> _company;
		private readonly HttpClient _httpClient;

		#pragma warning disable SKEXP0070
		private readonly Kernel _kernel;
		#pragma warning restore SKEXP0070 

		#pragma warning disable SKEXP0070 
		public DataScraper(MongoDbContext context, Kernel kernel)
		#pragma warning restore SKEXP0070 
		{
			_job = context.GetCollection<Job>("jobs");
			_company = context.GetCollection<Company>("companies");
			_httpClient = new HttpClient();
			_kernel = kernel;
		}

		public async Task ScrapeAllPagesAsync()
		{
			var today = DateTime.Today;

			for (int page = 1; page <= 30; page++)
			{
				var url = $"https://studentski-poslovi.hr/pretraga?category=sve-kategorije&province=sve-zupanije&search=Pretra%C5%BEi%20poslove&page={page}";
				var html = await _httpClient.GetStringAsync(url);
				var doc = new HtmlDocument();
				doc.LoadHtml(html);

				var marker = doc.DocumentNode
						.SelectSingleNode("//p[contains(@class,'-mb-4')][b[text()='Rezultati pretraživanja']]");

				IEnumerable<HtmlNode> ads;
				if (marker != null)
				{
					ads = marker.SelectNodes("following::div[contains(@class,'job-post') and @data-url]")
					       ?? Enumerable.Empty<HtmlNode>();
				}
				else
				{
					ads = Enumerable.Empty<HtmlNode>();
				}

				foreach (var ad in ads)
				{
					var link = ad.GetAttributeValue("data-url", "").Trim();
					if (string.IsNullOrEmpty(link))
						continue;

					var h5 = ad.SelectSingleNode(".//h5");
					string raw = h5?.InnerText.Trim() ?? "";
					var parts = raw.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
					string icon = parts.Length > 0 ? parts[0] : "";
					string jobTitle = parts.Length > 1 ? parts[1] : "";
					IconToJobType.TryGetValue(icon, out var jobType);
					jobType ??= "Razno";

					var compSpan = ad.SelectSingleNode(".//span[contains(@class,'text-sm') and contains(@class,'font-medium')]");
					string companyName = compSpan?.InnerText.Trim() ?? "";
					var idx = companyName.IndexOf(" vl", StringComparison.OrdinalIgnoreCase);
					if (idx > 0)
						companyName = companyName[..idx].Trim();

					var paySpan = ad.SelectSingleNode(".//i[contains(@class,'uil-money-bill')]/parent::span");
					string payRaw = paySpan?.InnerText ?? "";
					string payText = HtmlEntity.DeEntitize(payRaw)
						.Replace("€", "")
						.Replace("\u00A0", " ")
						.Trim();

					if (payText.Contains('-'))
						payText = payText.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

					double hourlyPay = 0;
					var m = Regex.Match(payText, "[\\d\\s\\.,]+");
					if (m.Success)
					{
						string num = m.Value;
						num = num.Replace(" ", "").Replace("\u00A0", "");

						int idxDot = num.LastIndexOf('.');
						int idxComma = num.LastIndexOf(',');
						char? decimalSep = null;

						if (idxDot >= 0 && idxComma >= 0)
						{
							decimalSep = idxComma > idxDot ? ',' : '.';
						}
						else if (idxDot >= 0)
						{
							int after = num.Length - idxDot - 1;
							decimalSep = (after > 0 && after <= 2) ? '.' : null;
						}
						else if (idxComma >= 0)
						{
							int after = num.Length - idxComma - 1;
							decimalSep = (after > 0 && after <= 2) ? ',' : null;
						}

						if (decimalSep != null)
						{
							var sb = new StringBuilder();
							foreach (var c in num)
							{
								if (char.IsDigit(c)) sb.Append(c);
								else if (c == decimalSep) sb.Append('.');
							}
							var normalized = sb.ToString();
							double.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out hourlyPay);
						}
						else
						{
							var digits = Regex.Replace(num, "\\D", "");
							if (!string.IsNullOrEmpty(digits))
								double.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out hourlyPay);
						}
					}

					var locSpan = ad.SelectSingleNode(".//i[contains(@class,'uil-map-marker')]/parent::span");
					string placeOfWork = locSpan?.InnerText.Trim() ?? "";

					await ScrapeJobAsync(new Uri(link), jobType, jobTitle, companyName, hourlyPay, placeOfWork, today);
				}
			}

		}

		static readonly Dictionary<string, string> IconToJobType = new()
		{
			{ "&#128194;", "Administrativni poslovi" },
			{ "&#127911;", "Anketiranje / Služba za korisnike" },
			{ "&#128176;", "Ekonomija, računovodstvo i financije" },
			{ "&#128170;", "Fizički poslovi" },
			{ "&#128187;", "IT poslovi, elektrotehnika i strojarstvo" },
			{ "&#128226;", "Promotivne aktivnosti" },
			{ "&#129395;", "Rad s djecom/animator" },
			{ "&#127981;", "Rad u skladištu/proizvodnji" },
			{ "&#10067;", "Razno" },
			{ "&#127891;", "Studentske prakse" },
			{ "&#128666;", "Transport i dostava" },
			{ "&#128722;", "Trgovina" },
			{ "&#9749;", "Turizam i ugostiteljstvo" },
			{ "&#128138;", "Zdravstvo" },
			{ "&#127979;", "Znanost i obrazovanje" }
		};

		private async Task ScrapeJobAsync(Uri jobUri, string jobType, string jobTitle, string companyName, double hourlyPay, string placeOfWork, DateTime today)
		{
			var html = await _httpClient.GetStringAsync(jobUri);
			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			var detailDiv = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'grid md:grid-cols-12 grid-cols-1 gap-[30px]')]");
			if (detailDiv == null) return;

			var pubSpan = detailDiv.SelectSingleNode(
				".//div[contains(@class,'ms-4')][p[text()='Datum objave:']]/span"
			);

			if (pubSpan == null)
				return;

			var pubText = pubSpan.InnerText.Trim() ?? "";      
			if (!DateTime.TryParseExact(
					pubText,
					"dd.MM.yyyy.",
					CultureInfo.InvariantCulture,
					DateTimeStyles.None,
					out var pubDate
				)
			)
			{
				return;
			}

			DateTime startDate = pubDate;
			bool gotStart = false;
			var startSpan = detailDiv.SelectSingleNode(".//div[contains(@class,'ms-4')][p[text()='Početak obavljanja posla:']]/span");
			string dateText = HtmlEntity.DeEntitize(startSpan?.InnerText ?? "").Trim();

			string[] formats = new[] { "dd.MM.yyyy.", "d.M.yyyy.", "dd.MM.yyyy", "d.M.yyyy" };
			if (!string.IsNullOrWhiteSpace(dateText))
			{
				gotStart = DateTime.TryParseExact(dateText, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate);
			}

			if (!gotStart)
			{
				var pNodes = detailDiv.SelectNodes(".//p[strong]");
				if (pNodes != null)
				{
					foreach (var p in pNodes)
					{
						var strong = p.SelectSingleNode(".//strong");
						var strongText = strong?.InnerText?.Trim() ?? "";
						if (string.Equals(strongText, "Početak rada", StringComparison.OrdinalIgnoreCase) || strongText.StartsWith("Početak rada", StringComparison.OrdinalIgnoreCase))
						{
							var pText = HtmlEntity.DeEntitize(p.InnerText ?? "").Trim();
							var alt = pText.Length > strongText.Length ? pText.Substring(strongText.Length).Trim() : string.Empty;
							alt = alt.Replace("\u00A0", " ").Trim();

							if (!string.IsNullOrWhiteSpace(alt))
							{
								if (DateTime.TryParseExact(alt, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate))
								{
									gotStart = true;
									break;
								}
								if (DateTime.TryParse(alt, CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate))
								{
									gotStart = true;
									break;
								}
							}
						}
					}
				}
			}

			if (!gotStart)
			{
				startDate = pubDate;
			}

			var opisHeader = detailDiv.SelectSingleNode(".//h6[contains(@class,'text-lg') and normalize-space(text())='Opis:' ]");
			string rawText;
			if (opisHeader != null)
			{
				var sb = new StringBuilder();
				for (var node = opisHeader.NextSibling; node != null; node = node.NextSibling)
				{
					if (node.Name == "h6") break;
					if (node.NodeType == HtmlNodeType.Text || node.NodeType == HtmlNodeType.Element)
						sb.AppendLine(node.InnerText.Trim());
				}
				rawText = sb.ToString().Trim();
			}
			else
			{
				rawText = detailDiv.InnerText.Trim();
			}

			string? description = null;
			var strongNodes = detailDiv.SelectNodes(".//strong");
			if (strongNodes != null)
			{
				var paragraphs = new List<string>();
				foreach (var sn in strongNodes)
				{
					var txt = sn.InnerText?.Trim() ?? "";
					if (txt.StartsWith("Opis posla", StringComparison.OrdinalIgnoreCase) || txt.StartsWith("Detaljan opis posla", StringComparison.OrdinalIgnoreCase))
					{
						var localSb = new StringBuilder();
						for (var n = sn.NextSibling; n != null; n = n.NextSibling)
						{
							if (n.NodeType == HtmlNodeType.Element && n.SelectSingleNode(".//strong") != null) break;
							if (n.NodeType == HtmlNodeType.Text || n.NodeType == HtmlNodeType.Element)
						{
							var t = HtmlEntity.DeEntitize(n.InnerText).Trim();
							if (!string.IsNullOrWhiteSpace(t))
								localSb.Append(t + " ");
						}
						}

						var parent = sn.ParentNode;
						for (var p = parent.NextSibling; p != null; p = p.NextSibling)
						{
							if (p.NodeType == HtmlNodeType.Element && p.SelectSingleNode(".//strong") != null)
								break;
							if (p.NodeType == HtmlNodeType.Text || p.NodeType == HtmlNodeType.Element)
						{
							var t = HtmlEntity.DeEntitize(p.InnerText).Trim();
							if (!string.IsNullOrWhiteSpace(t))
								paragraphs.Add(t);
						}
						}

						var localText = HtmlEntity.DeEntitize(localSb.ToString()).Trim();
						if (!string.IsNullOrWhiteSpace(localText))
							paragraphs.Insert(0, localText);
					}
				}

				if (paragraphs.Any())
				{
					description = string.Join("\n\n", paragraphs.Select(p => Regex.Replace(p, "\u00A0", " ").Trim()));
				}
			}

			var daysInMonth = DateTime.DaysInMonth(startDate.Year, startDate.Month);

			var startDateUtc = DateTime.SpecifyKind(startDate.Date, DateTimeKind.Utc);
			var endDateUtc = DateTime.SpecifyKind(new DateTime(startDate.Year, startDate.Month, daysInMonth).Date, DateTimeKind.Utc);

			var newJobId = ObjectId.GenerateNewId();

			var filter = Builders<Company>.Filter.Regex(
				c => c.Name,
				new BsonRegularExpression($"^{Regex.Escape(companyName)}$", "i")
			);

			var existingCompany = await _company
				.Find(filter)
				.FirstOrDefaultAsync();

			ObjectId companyId;

			if (existingCompany != null)
			{
				companyId = existingCompany.CompanyId;

				var jobFilter = Builders<Job>.Filter.And(
					Builders<Job>.Filter.Regex(j => j.Title, new BsonRegularExpression($"^{Regex.Escape(jobTitle)}$", "i")),
					Builders<Job>.Filter.Eq(j => j.CompanyId, companyId),
					Builders<Job>.Filter.Regex(j => j.PlaceOfWork, new BsonRegularExpression($"^{Regex.Escape(placeOfWork)}$", "i"))
				);

				var existingJob = await _job.Find(jobFilter).FirstOrDefaultAsync();
				if (existingJob != null)
				{
					return;
				}
			}
			else
			{
				companyId = ObjectId.GenerateNewId();
			}

			var requiredTraits = await RateLimitedCaller.CallWithRateLimitAsync(() => ExtractRequiredTraitsWithGeminiAsync(rawText));

			var job = new Job
			{
				JobId = newJobId,
				CompanyId = companyId,
				Title = jobTitle,
				Type = jobType,
				HourlyPay = hourlyPay,
				StartDate = startDateUtc,
				EndDate = endDateUtc,
				PlaceOfWork = placeOfWork,
				RequiredTraits = requiredTraits,
				Description = description
			};

			job.CompanyId = companyId;
			await _job.InsertOneAsync(job);

			if (existingCompany == null)
			{
				var newCompany = new Company
				{
					CompanyId = companyId,
					Name = companyName,
					JobAds = new List<ObjectId> { job.JobId },
					ThumbsReceived = null
				};
				await _company.InsertOneAsync(newCompany);
			}
			else
			{
				var update = Builders<Company>.Update.AddToSet(c => c.JobAds, job.JobId);
				await _company.UpdateOneAsync(c => c.CompanyId == existingCompany.CompanyId, update);
			}
		}

		private async Task<List<string>?> ExtractRequiredTraitsWithGeminiAsync(string rawText)
		{
			var extractor = new TwoPassJobSkillsExtractor(_kernel);
			var normalized = await extractor.ExtractSkillsAsync(rawText);

			if (string.IsNullOrWhiteSpace(normalized))
				return null;

			var list = normalized.Split(',')
				.Select(s => s.Trim())
				.Where(s => !string.IsNullOrWhiteSpace(s))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			return list.Count > 0 ? list : null;
		}

		public static class RateLimitedCaller
		{
			private static readonly ConcurrentQueue<DateTime> _timestamps = new();
			private static readonly object _lock = new();
			
			public static async Task<T> CallWithRateLimitAsync<T>(Func<Task<T>> factory)
			{
				while (true)
				{
					DateTime now = DateTime.UtcNow;

					lock (_lock)
					{
						while (_timestamps.TryPeek(out var ts) && (now - ts).TotalSeconds >= 90)
						{
							_timestamps.TryDequeue(out _);
						}

						if (_timestamps.Count < 10)
						{
							_timestamps.Enqueue(now);
							break;
						}
					}

					DateTime oldest = _timestamps.TryPeek(out var t) ? t : now;
					double waitMs = 1000 * (90 - (now - oldest).TotalSeconds);
					if (waitMs < 0) waitMs = 0;
					await Task.Delay((int)waitMs);
				}

				return await factory();
			}
		}

		private class TwoPassJobSkillsExtractor
		{
			private readonly Kernel _kernel;

			public TwoPassJobSkillsExtractor(Kernel kernel)
			{
				_kernel = kernel;
			}

			private const string ControlledVocabularyPrompt = @"
			You are an expert HR analyst. Extract ONLY personality traits and soft skills that are EXPLICITLY mentioned in the job description.

			CRITICAL RULES:
			1. A trait must be EXPLICITLY written in the text (not implied or inferred)
			2. Return ONLY traits from the allowed list below
			3. Return traits as comma-separated values in nominative singular
			4. If NO traits from the list are found, return EXACTLY: Ništa
			5. Do NOT add explanations, preambles, or any other text

			ALLOWED TRAITS (return ONLY these if explicitly found):
			Komunikativnost, Iskrenost, Poštenje, Druželjubivost, Odgovornost, Pouzdanost, Marljivost, 
			Adaptabilnost, Otpornost, Fleksibilnost, Samomotiviranost, Entuzijazam, Kreativnost, 
			Kritičko razmišljanje, Inovativnost, Timski rad, Empatija, Organizacija, Otvorenost, 
			Profesionalnost, Etičnost, Strpljenje, Ambicija, Odlučnost, Digitalna spretnost, Urednost, 
			Smisao za ljepotu, Samostalnost, Motiviranost, Detaljnost, Samopouzdanost, Snalažljivost, 
			Predanost, Elokventnost, Sistematičnost, Pristojnost, Dosljednost, Preciznost, Pozitivnost, 
			Točnost, Spretnost

			MATCHING RULES:
			- ""komunikacijske vještine"" → Komunikativnost
			- ""rad u timu"", ""timski igrač"" → Timski rad
			- ""fleksibilan pristup"" → Fleksibilnost
			- ""sposobnost organizacije"" → Organizacija
			- ""kreativno rješavanje"" → Kreativnost
			- ""pouzdan"", ""pouzdana osoba"" → Pouzdanost
			- ""odgovorna osoba"" → Odgovornost
			- ""preciznost u radu"" → Preciznost

			EXAMPLES:

			Input: ""Tražimo komunikativnu osobu s kreativnim pristupom i sposobnošću timskog rada.""
			Output: Komunikativnost, Kreativnost, Timski rad

			Input: ""Potrebna fleksibilnost, adaptabilnost i visoka razina profesionalnosti.""
			Output: Fleksibilnost, Adaptabilnost, Profesionalnost

			Input: ""Senior developer s 5+ godina iskustva u Pythonu i AWS.""
			Output: Ništa

			Input: ""Odgovorna i pouzdana osoba s empatijom prema kolegama i pažnjom na detalje.""
			Output: Odgovornost, Pouzdanost, Empatija, Detaljnost

			JOB DESCRIPTION:
			{{$input}}

			OUTPUT (comma-separated traits or Ništa):";

			private const string OpenExtractionPrompt = @"
			You are an expert HR analyst. Extract ALL personality traits, soft skills, competencies, and values that are EXPLICITLY mentioned in the job description.

			CRITICAL RULES:
			1. Extract ONLY what is EXPLICITLY written (not implied)
			2. Convert to abstract nouns in Croatian nominative singular
			3. Return as comma-separated values
			4. Use the SAME FORMAT as these examples
			5. If NOTHING is found, return EXACTLY: Ništa

			FORMAT EXAMPLES (follow this style):
			- komunikativnost, kreativnost, timski rad, fleksibilnost
			- analitičko razmišljanje, organizacija, preciznost
			- Python, SQL, .NET, vodstvo, strateško planiranje

			GRAMMATICAL RULES:
			- Nominative singular: ""komunikativnost"" (not ""komunikativnosti"")
			- Abstract nouns: ""organizacija"" (not ""organizirati"")
			- Keep technical terms: ""Python"", ""Scrum"", ""SQL""
			- Noun phrases: ""analitičko razmišljanje"", ""timski rad"", ""kritičko razmišljanje""

			CONVERSION EXAMPLES:
			- ""komunikacijske vještine"" → komunikativnost
			- ""sposobnost timskog rada"" → timski rad
			- ""kreativno rješavanje problema"" → kreativnost, rješavanje problema
			- ""poznavanje Pythona"" → Python
			- ""vođenje tima"" → vodstvo 
			- ""organizacija vremena"" → organizacija, upravljanje vremenom

			EXAMPLES:

			Input: ""Tražimo senior backend developera s iskustvom u C# i .NET-u. Potrebna je sposobnost mentorstva i proaktivan pristup rješavanju problema.""
			Output: C#, .NET, mentorstvo, proaktivnost, rješavanje problema

			Input: ""Marketing manager s izvrsnim prezentacijskim vještinama i poznavanjem Google Ads platforme. Strateško razmišljanje i analiza podataka su ključni.""
			Output: prezentacijske vještine, Google Ads, strateško razmišljanje, analiza podataka

			Input: ""Junior developer - prvi posao nakon faksa. Dolazite u dinamičnu kompaniju.""
			Output: Ništa

		 Input: ""Potrebna je osoba s integritetom, analitičkim razmišljanjem i empatijom prema kolegama.""
			Output: integritet, analitičko razmišljanje, empatija

			JOB DESCRIPTION:
			{{$input}}

			OUTPUT (comma-separated skills or Ništa):";

			public async Task<string?> ExtractSkillsAsync(string jobDescription)
			{
				var controlledResult = await ExecuteExtractionAsync(
					jobDescription,
					ControlledVocabularyPrompt,
					temperature: 0.1
				);

				var normalizedControlled = NormalizeSkills(controlledResult);
				if (normalizedControlled != null)
				{
					return normalizedControlled;
				}

				var openResult = await ExecuteExtractionAsync(
					jobDescription,
					OpenExtractionPrompt,
					temperature: 0.2
				);

				return NormalizeSkills(openResult);
			}

			private async Task<string> ExecuteExtractionAsync(
				string jobDescription,
				string promptTemplate,
				double temperature)
			{
				var executionSettings = new OpenAIPromptExecutionSettings
				{
					Temperature = temperature,
					MaxTokens = 400,
					TopP = 0.9
				};

				var function = _kernel.CreateFunctionFromPrompt(promptTemplate, executionSettings);

				var result = await _kernel.InvokeAsync(function, new()
				{
					["input"] = jobDescription
				});

				return result.ToString().Trim();
			}

			private string? NormalizeSkills(string skills)
			{
				if (string.IsNullOrWhiteSpace(skills))
					return null;

				skills = Regex.Replace(skills, @"^(Output:|Rezultat:|Skills:)\s*", "", RegexOptions.IgnoreCase);
				skills = Regex.Replace(skills, @"```.*?```", "", RegexOptions.Singleline);
				skills = skills.Trim('`', '"', '\'', ' ', '\n', '\r');

				if (skills.Equals("Ništa", StringComparison.OrdinalIgnoreCase))
					return null;

				var skillList = skills.Split(',')
					.Select(s => s.Trim())
					.Where(s => !string.IsNullOrWhiteSpace(s))
					.Select(CleanSkill)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				return skillList.Count > 0 ? string.Join(", ", skillList) : null;
			}

			private string CleanSkill(string skill)
			{
				skill = Regex.Replace(skill, @"\s+", " ").Trim();

				if (skill.Length > 0)
				{
					skill = char.ToUpper(skill[0]) + skill.Substring(1).ToLower();
				}

				return skill;
			}
		}

	}

}
