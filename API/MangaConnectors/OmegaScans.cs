using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using API.Exceptions;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using Newtonsoft.Json.Linq;

namespace API.MangaConnectors;

// NOTE: For best results on large downloads, REQUESTS_PER_MINUTE should be set to 60/min over the default 90/min
// It will download at 90, but will fail multiple times throughout download due to rate limits.
public class OmegaScans : MangaConnector
{
    public OmegaScans() : base(
		"OmegaScans", 
        ["en"],
        ["omegascans.org"], 
        "https://omegascans.org/favicon.ico",
		true
		)
    {
        this.downloadClient = new HttpDownloadClient();
    }

    // Search manga using OmegaScans API
    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching Obj: {0}", mangaSearchName);
        List<(Manga, MangaConnectorId<Manga>)> mangas = new ();
        
        int page = 1;
        int lastPage = int.MaxValue;
        while(page <= lastPage)
        {
            string requestUrl =
                $"https://api.omegascans.org/query?adult=true&query_string={HttpUtility.UrlEncode(mangaSearchName)}&page={page}";

            HttpResponseMessage result = downloadClient.MakeRequest(requestUrl, RequestType.MangaDexFeed).Result;
            if ((int)result.StatusCode < 200 || (int)result.StatusCode >= 300)
            {
                Log.Error("Request failed");
                return [];
            }

            using StreamReader sr = new (result.Content.ReadAsStream());
            JObject jObject = JObject.Parse(sr.ReadToEnd());

            lastPage = jObject["meta"]?["last_page"].Value<int>() ?? 1;
			
            JArray? data = jObject["data"] as JArray;
            if (data is null)
            {
                Log.Error("Data was null");
                return [];
            }
            
            foreach (var mangaData in data)
			{
				// Getting manga data from ID (slug) over search results as it has more details
				string mangaIdOnSite = mangaData["series_slug"]?.ToString();
				if (string.IsNullOrEmpty(mangaIdOnSite)) continue;

				var mangaDetails = GetMangaFromId(mangaIdOnSite);
				if (mangaDetails.HasValue)
				{
					mangas.Add(mangaDetails.Value);
				}
			}
			page++;
        }
        
        Log.InfoFormat("Search {0} yielded {1} results.", mangaSearchName, mangas.Count);
        return mangas.ToArray();
    }

	// Get manga by URL
    private static readonly Regex GetMangaIdFromUrl = new(@"https?:\/\/omegascans\.org\/series\/([a-z0-9-]+)\/?.*");
    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        Log.InfoFormat("Getting Obj: {0}", url);
        if (!UrlMatchesConnector(url))
        {
            Log.DebugFormat("Url is not for Connector. {0}", url);
            return null;
        }

        Match match = GetMangaIdFromUrl.Match(url);
        if (!match.Success || !match.Groups[1].Success)
        {
            Log.DebugFormat("Url is not for Connector (Could not retrieve id). {0}", url);
            return null;
        }
        string slug = match.Groups[1].Value;

        return GetMangaFromId(slug);
    }

	//Get manga by ID (slug)
    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        Log.InfoFormat("Getting Obj: {0}", mangaIdOnSite);
        string requestUrl =
            $"https://api.omegascans.org/series/{mangaIdOnSite}";
        
        HttpResponseMessage result = downloadClient.MakeRequest(requestUrl, RequestType.MangaDexFeed).Result;
        if ((int)result.StatusCode < 200 || (int)result.StatusCode >= 300)
        {
            Log.Error("Request failed");
            return null;
        }

        using StreamReader sr = new (result.Content.ReadAsStream());
        JObject jObject = JObject.Parse(sr.ReadToEnd());

        string? title = jObject["title"]?.ToString();
		string? description = jObject["description"]?.ToString();
		string? status = jObject["status"]?.ToString();
		uint? releaseYear = jObject["release_year"]?.Value<uint?>();
		string? coverUrl = jObject["thumbnail"]?.ToString();
		string? websiteUrl = $"https://omegascans.org/series/{mangaIdOnSite}";
		string? seriesId = jObject["id"]?.ToString();
		
		string? authorString = jObject["author"]?.ToString();
		List<Author> authors = new();
		if (!string.IsNullOrEmpty(authorString))
		{
			var authorNames = authorString.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
										  .Select(a => a.Trim())
										  .Where(a => !string.IsNullOrEmpty(a))
										  .Select(a => new Author(a))
										  .ToList();
			authors.AddRange(authorNames);
		}
		
		List<MangaTag> tags = new();
		JArray? tagsArray = jObject["tags"] as JArray;
		if (tagsArray != null)
		{
			tags = tagsArray
				.Select(tag => tag["name"]?.ToString())
				.Where(name => !string.IsNullOrEmpty(name))
				.Select(name => new MangaTag(name))
				.ToList();
		}
		
		MangaReleaseStatus releaseStatus = status switch
        {
            "Completed" => MangaReleaseStatus.Completed,
            "Ongoing" => MangaReleaseStatus.Continuing,
            "Cancelled" => MangaReleaseStatus.Cancelled,
            "Hiatus" => MangaReleaseStatus.OnHiatus,
			"Dropped" => MangaReleaseStatus.Cancelled, //Scanlator dropped it; need to check if status exists in Tranga.
            _ => MangaReleaseStatus.Unreleased
        };
		
		Manga manga = new Manga(title, description, coverUrl, releaseStatus, authors, tags, null, null, null, 0f, releaseYear, null);

		MangaConnectorId<Manga> mcId = new(manga, this, seriesId, websiteUrl);
		manga.MangaConnectorIds.Add(mcId);

		return (manga, mcId);
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        Log.InfoFormat("Getting Chapters: {0}", mangaId.IdOnConnectorSite);
        List<(Chapter, MangaConnectorId<Chapter>)> chapters = new ();
        
        int page = 1;
        int lastPage = int.MaxValue;
        while(page <= lastPage)
        {
            string requestUrl =
                $"https://api.omegascans.org/chapter/query?page={page}&perPage=30&series_id={mangaId.IdOnConnectorSite}";

            HttpResponseMessage result = downloadClient.MakeRequest(requestUrl, RequestType.MangaDexFeed).Result;
            if ((int)result.StatusCode < 200 || (int)result.StatusCode >= 300)
            {
                Log.Error("Request failed");
                return [];
            }

            using StreamReader sr = new (result.Content.ReadAsStream());
            JObject jObject = JObject.Parse(sr.ReadToEnd());

            lastPage = jObject["meta"]?["last_page"].Value<int>() ?? 1;
			
            JArray? data = jObject["data"] as JArray;
            if (data is null)
            {
                Log.Error("Data was null");
                return new (Chapter, MangaConnectorId<Chapter>)[] { };
            }
            
            chapters.AddRange(
				data
					.Select(d => ParseChapterFromJToken(mangaId, d))
					.Where(c => c.HasValue)
					.Select(c => c.Value)
			);
			
			page++;
        }
        
        Log.InfoFormat("Request for chapters for {0} yielded {1} results.", mangaId.Obj.Name, chapters.Count);
        return chapters.ToArray();
    }
	
	private static readonly Regex GetChapterIdFromUrl = new(@"https?:\/\/omegascans\.org\/series\/[a-z0-9-]+\/chapter-([0-9]+)\/?.*");
    // =========================
    // IMAGES
    // =========================
    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
		Log.InfoFormat("Getting Chapter Image-Urls: {0}", chapterId.Obj);
        if (chapterId.WebsiteUrl is null)
        {
            Log.Error("Chapter URL is null");
            return [];
        }
		
		string? referrer = null;
        if (chapterId.Obj.ParentManga.MangaConnectorIds is not null && chapterId.Obj.ParentManga.MangaConnectorIds.Any())
        {
            referrer = chapterId.Obj.ParentManga.MangaConnectorIds
                .FirstOrDefault(id => id.MangaConnectorName == this.Name)?.WebsiteUrl;
        }
		
		return GetChapterImageUrlsAsync(chapterId, referrer).GetAwaiter().GetResult();
	}
	
	private async Task<string[]> GetChapterImageUrlsAsync(MangaConnectorId<Chapter> chapterId, string? referrer)
	{
		await using ChromiumDownloadClient chromium = new ChromiumDownloadClient();
		
        HttpResponseMessage response = await chromium.MakeRequest(chapterId.WebsiteUrl!, RequestType.Default, referrer);

        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
		{
			Log.Error("Failed to load chapter page with Chromium");
			return [];
		}

        string html = await response.Content.ReadAsStringAsync();
		
		HtmlDocument doc = new();
		doc.LoadHtml(html);
		
        HtmlNodeCollection? imageNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'flex flex-col justify-center items-center')]//img");

        if (imageNodes is null || imageNodes.Count == 0)
		{
			Log.Warn("No chapter page images found");
			return [];
		}
		
		string[] imageUrls = imageNodes
			.Select(i => 
			{
				string src = i.GetAttributeValue("src", "");
				if (string.IsNullOrEmpty(src))
					src = i.GetAttributeValue("data-src", "");
				return src;
			})
			.Where(u => !string.IsNullOrEmpty(u))
			.ToArray();
			
		Log.InfoFormat("Found {0} images for chapter {1}", imageUrls.Length, chapterId.Obj);
		return imageUrls;
    }

    private (Chapter chapter, MangaConnectorId<Chapter> id)? ParseChapterFromJToken(MangaConnectorId<Manga> mcIdManga, JToken jToken)
    {
		int price = jToken.Value<int>("price");
		
		// Skip premium chapters
		if (price != 0)
		{
			Log.InfoFormat("Skipping chapter {0} because the price is not 0 (price: {1})", jToken.Value<string>("chapter_name"), price);
			return null;
		}
		
        string? id = jToken.Value<string>("id");
		string? chapterName = jToken.Value<string>("chapter_name");
		string? chapterSlug = jToken.Value<string>("chapter_slug");
		string? chapterThumbnail = jToken.Value<string>("chapter_thumbnail");
		string? createdAt = jToken.Value<string>("created_at");
		string? seriesSlug = jToken["series"]?["series_slug"]?.ToString();
		string? chapterStr = chapterName?.Replace("Chapter", "").Trim();
		string? title = null;
        
        if(id is null || chapterStr is null)
            throw new ParsingException("jToken was not in expected format");
        
        string websiteUrl = $"https://omegascans.org/series/{seriesSlug}/{chapterSlug}";
        Chapter chapter = new (mcIdManga.Obj, chapterStr, null, title);
        MangaConnectorId<Chapter> mcId = new(chapter, this, id, websiteUrl);
        chapter.MangaConnectorIds.Add(mcId);
        return (chapter, mcId);
    }
}
