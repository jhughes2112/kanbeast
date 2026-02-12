using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace KanBeast.Worker.Services.Tools;

// Tools for fetching web pages and searching the web.
public static class WebTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient SharedHttpClient = new HttpClient();  // inherently thread safe and intended for reuse

	[Description("Fetch the contents of a web page at the specified URL. Returns the text content with HTML tags stripped.")]
    public static async Task<ToolResult> GetWebPageAsync(
        [Description("The fully-formed URL to fetch content from.")] string url,
        ToolContext context)
    {
        CancellationToken cancellationToken = WorkerSession.CancellationToken;
        ToolResult result;

        if (string.IsNullOrWhiteSpace(url))
        {
            result = new ToolResult("Error: URL cannot be empty.", false);
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            result = new ToolResult($"Error: Invalid URL format: {url}", false);
        }
        else
        {
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);
                HttpResponseMessage response = await SharedHttpClient.GetAsync(uri, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    result = new ToolResult($"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase} for URL: {url}", false);
                }
                else
                {
                    string html = await response.Content.ReadAsStringAsync(cts.Token);
                    string text = StripHtmlTags(html);

                    if (text.Length > 50000)
                    {
                        text = text.Substring(0, 50000) + "\n\n[Content truncated at 50000 characters]";
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        result = new ToolResult($"Error: No readable text content found at URL: {url}", false);
                    }
                    else
                    {
                        result = new ToolResult(text, false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Request timed out or cancelled for URL: {url}", false);
            }
            catch (HttpRequestException ex)
            {
                result = new ToolResult($"Error: Network error fetching URL {url}: {ex.Message}", false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to fetch URL {url}: {ex.Message}", false);
            }
        }

        return result;
    }

    [Description("Search the web for information using DuckDuckGo. Returns a list of search results with titles, URLs, and snippets.")]
    public static async Task<ToolResult> SearchWebAsync(
        [Description("The search query to use.")] string query,
        [Description("Maximum number of results to return (1-20). Pass empty string for default of 10.")] string maxResults,
        ToolContext context)
    {
        CancellationToken cancellationToken = WorkerSession.CancellationToken;
        ToolResult result;

        if (string.IsNullOrWhiteSpace(query))
        {
            result = new ToolResult("Error: Search query cannot be empty.", false);
        }
        else
        {
            int limit = 10;
            if (!string.IsNullOrWhiteSpace(maxResults))
            {
                int.TryParse(maxResults, out limit);
            }

            if (limit <= 0)
            {
                limit = 10;
            }

            if (limit > 20)
            {
                limit = 20;
            }

            try
            {
                result = new ToolResult(await SearchDuckDuckGoAsync(query, limit, cancellationToken), false);
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Search request timed out or cancelled for query: {query}", false);
            }
            catch (HttpRequestException ex)
            {
                result = new ToolResult($"Error: Network error during search: {ex.Message}", false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Search failed: {ex.Message}", false);
            }
        }

        return result;
    }

    private static async Task<string> SearchDuckDuckGoAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string encodedQuery = WebUtility.UrlEncode(query);
        string url = $"https://html.duckduckgo.com/html/?q={encodedQuery}";

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);
        HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cts.Token);

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
