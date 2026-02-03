using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

// Tools for fetching web pages and searching the web.
public class WebTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly string _searchProvider;
    private readonly string? _googleApiKey;
    private readonly string? _googleSearchEngineId;

    public WebTools(HttpClient httpClient, string searchProvider, string? googleApiKey, string? googleSearchEngineId)
    {
        _httpClient = httpClient;
        _searchProvider = searchProvider;
        _googleApiKey = googleApiKey;
        _googleSearchEngineId = googleSearchEngineId;
    }

    [KernelFunction("get_web_page")]
    [Description("Fetch the contents of a web page at the specified URL. Returns the text content with HTML tags stripped.")]
    public async Task<string> GetWebPageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Error: URL cannot be empty.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return $"Error: Invalid URL format: {url}";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            HttpResponseMessage response = await _httpClient.GetAsync(uri, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase} for URL: {url}";
            }

            string html = await response.Content.ReadAsStringAsync(cts.Token);
            string text = StripHtmlTags(html);

            if (text.Length > 50000)
            {
                text = text.Substring(0, 50000) + "\n\n[Content truncated at 50000 characters]";
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return $"Error: No readable text content found at URL: {url}";
            }

            return text;
        }
        catch (TaskCanceledException)
        {
            return $"Error: Request timed out after {DefaultTimeout.TotalSeconds} seconds for URL: {url}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Network error fetching URL {url}: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to fetch URL {url}: {ex.Message}";
        }
    }

    [KernelFunction("search_web")]
    [Description("Search the web for information. Returns a list of search results with titles, URLs, and snippets.")]
    public async Task<string> SearchWebAsync(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: Search query cannot be empty.";
        }

        if (maxResults <= 0)
        {
            maxResults = 10;
        }

        if (maxResults > 20)
        {
            maxResults = 20;
        }

        try
        {
            if (string.Equals(_searchProvider, "google", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(_googleApiKey) &&
                !string.IsNullOrEmpty(_googleSearchEngineId))
            {
                return await SearchGoogleAsync(query, maxResults);
            }
            else
            {
                return await SearchDuckDuckGoAsync(query, maxResults);
            }
        }
        catch (TaskCanceledException)
        {
            return $"Error: Search request timed out after {DefaultTimeout.TotalSeconds} seconds for query: {query}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Network error during search: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: Search failed: {ex.Message}";
        }
    }

    private async Task<string> SearchDuckDuckGoAsync(string query, int maxResults)
    {
        string encodedQuery = WebUtility.UrlEncode(query);
        string url = $"https://html.duckduckgo.com/html/?q={encodedQuery}";

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
        HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            return $"Error: DuckDuckGo returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }

        string html = await response.Content.ReadAsStringAsync(cts.Token);
        List<SearchResult> results = ParseDuckDuckGoResults(html, maxResults);

        if (results.Count == 0)
        {
            return $"No search results found for: {query}";
        }

        return FormatSearchResults(results);
    }

    private async Task<string> SearchGoogleAsync(string query, int maxResults)
    {
        string encodedQuery = WebUtility.UrlEncode(query);
        string url = $"https://www.googleapis.com/customsearch/v1?key={_googleApiKey}&cx={_googleSearchEngineId}&q={encodedQuery}&num={maxResults}";

        using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
        HttpResponseMessage response = await _httpClient.GetAsync(url, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            return $"Error: Google API returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }

        string json = await response.Content.ReadAsStringAsync(cts.Token);
        List<SearchResult> results = ParseGoogleResults(json, maxResults);

        if (results.Count == 0)
        {
            return $"No search results found for: {query}";
        }

        return FormatSearchResults(results);
    }

    private static List<SearchResult> ParseDuckDuckGoResults(string html, int maxResults)
    {
        List<SearchResult> results = new List<SearchResult>();

        Regex resultRegex = new Regex(
            @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>([^<]+)</a>.*?<a[^>]+class=""result__snippet""[^>]*>([^<]*)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        MatchCollection matches = resultRegex.Matches(html);

        foreach (Match match in matches)
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            string rawUrl = match.Groups[1].Value;
            string title = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            string snippet = WebUtility.HtmlDecode(match.Groups[3].Value.Trim());

            string actualUrl = rawUrl;
            if (rawUrl.Contains("uddg="))
            {
                Match urlMatch = Regex.Match(rawUrl, @"uddg=([^&]+)");
                if (urlMatch.Success)
                {
                    actualUrl = WebUtility.UrlDecode(urlMatch.Groups[1].Value);
                }
            }

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(actualUrl))
            {
                results.Add(new SearchResult
                {
                    Title = title,
                    Url = actualUrl,
                    Snippet = snippet
                });
            }
        }

        return results;
    }

    private static List<SearchResult> ParseGoogleResults(string json, int maxResults)
    {
        List<SearchResult> results = new List<SearchResult>();

        using JsonDocument doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out JsonElement items))
        {
            return results;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            string title = item.TryGetProperty("title", out JsonElement titleEl) ? titleEl.GetString() ?? "" : "";
            string url = item.TryGetProperty("link", out JsonElement linkEl) ? linkEl.GetString() ?? "" : "";
            string snippet = item.TryGetProperty("snippet", out JsonElement snippetEl) ? snippetEl.GetString() ?? "" : "";

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                results.Add(new SearchResult
                {
                    Title = title,
                    Url = url,
                    Snippet = snippet
                });
            }
        }

        return results;
    }

    private static string FormatSearchResults(List<SearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No search results found.";
        }

        StringBuilder sb = new StringBuilder();
        int index = 1;

        foreach (SearchResult result in results)
        {
            sb.AppendLine($"{index}. {result.Title}");
            sb.AppendLine($"   URL: {result.Url}");
            if (!string.IsNullOrEmpty(result.Snippet))
            {
                sb.AppendLine($"   {result.Snippet}");
            }
            sb.AppendLine();
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string StripHtmlTags(string html)
    {
        html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"\s+", " ");
        html = Regex.Replace(html, @"(\r?\n\s*){3,}", "\n\n");

        return html.Trim();
    }

    private class SearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
    }
}
