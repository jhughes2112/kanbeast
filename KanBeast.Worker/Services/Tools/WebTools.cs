using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KanBeast.Worker.Services.Tools;

// Tools for fetching web pages and searching the web.
public static class WebTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);
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
            result = new ToolResult("Error: URL cannot be empty.", false, false);
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            result = new ToolResult($"Error: Invalid URL format: {url}", false, false);
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
                    result = new ToolResult($"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase} for URL: {url}", false, false);
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
                        result = new ToolResult($"Error: No readable text content found at URL: {url}", false, false);
                    }
                    else
                    {
                        result = new ToolResult(text, false, false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Request timed out or cancelled for URL: {url}", false, false);
            }
            catch (HttpRequestException ex)
            {
                result = new ToolResult($"Error: Network error fetching URL {url}: {ex.Message}", false, false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to fetch URL {url}: {ex.Message}", false, false);
            }
        }

        return result;
    }

    [Description("Search the web for information via OpenRouter's web search plugin. Returns a list of search results with titles, URLs, and snippets.")]
    public static async Task<ToolResult> SearchWebAsync(
        [Description("The search query to use.")] string query,
        [Description("Maximum number of results to return (1-20). Pass empty string for default of 5.")] string maxResults,
        ToolContext context)
    {
        CancellationToken cancellationToken = WorkerSession.CancellationToken;
        ToolResult result;

        if (string.IsNullOrWhiteSpace(WorkerSession.ApiKey))
        {
            result = new ToolResult("Error: Web search is not configured. Set an OpenRouter API key in Settings.", false, false);
        }
        else if (string.IsNullOrWhiteSpace(query))
        {
            result = new ToolResult("Error: Search query cannot be empty.", false, false);
        }
        else
        {
            int limit = 5;
            if (!string.IsNullOrWhiteSpace(maxResults))
            {
                int.TryParse(maxResults, out limit);
            }

            if (limit <= 0)
            {
                limit = 5;
            }

            if (limit > 20)
            {
                limit = 20;
            }

            try
            {
                result = new ToolResult(await SearchViaOpenRouterAsync(query, limit, cancellationToken), false, false);
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Search request timed out or cancelled for query: {query}", false, false);
            }
            catch (HttpRequestException ex)
            {
                result = new ToolResult($"Error: Network error during search: {ex.Message}", false, false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Search failed: {ex.Message}", false, false);
            }
        }

        return result;
    }

    private static async Task<string> SearchViaOpenRouterAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string apiKey = WorkerSession.ApiKey;
        string model = WorkerSession.WebSearch.Model;
        string engine = WorkerSession.WebSearch.Engine;

        // Build the plugin block. "auto" means omit engine so OpenRouter picks native-or-exa.
        string pluginJson;
        if (string.Equals(engine, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(engine))
        {
            pluginJson = $"{{\"id\":\"web\",\"max_results\":{maxResults}}}";
        }
        else
        {
            pluginJson = $"{{\"id\":\"web\",\"engine\":\"{engine}\",\"max_results\":{maxResults}}}";
        }

        string requestJson = $"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\",\"content\":{JsonSerializer.Serialize(query)}}}],\"plugins\":[{pluginJson}],\"temperature\":0,\"max_tokens\":1024}}";

        string endpoint = WorkerSession.Endpoint.TrimEnd('/');
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(SearchTimeout);
        HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cts.Token);
        string responseBody = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            return $"Error: OpenRouter returned HTTP {(int)response.StatusCode}: {responseBody}";
        }

        return ParseOpenRouterSearchResponse(responseBody);
    }

    private static string ParseOpenRouterSearchResponse(string json)
    {
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // Check for API-level error.
        if (root.TryGetProperty("error", out JsonElement errorEl))
        {
            string errorMsg = errorEl.TryGetProperty("message", out JsonElement msgEl) ? msgEl.GetString() ?? "Unknown error" : "Unknown error";
            return $"Error: OpenRouter search failed: {errorMsg}";
        }

        // Extract assistant content.
        string content = "";
        if (root.TryGetProperty("choices", out JsonElement choices))
        {
            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out JsonElement message))
                {
                    if (message.TryGetProperty("content", out JsonElement contentEl))
                    {
                        content = contentEl.GetString() ?? "";
                    }
                }

                break;
            }
        }

        // Extract annotations (url_citation) for structured results.
        List<string> citations = new List<string>();
        if (root.TryGetProperty("choices", out JsonElement choices2))
        {
            foreach (JsonElement choice in choices2.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("annotations", out JsonElement annotations))
                {
                    int index = 1;
                    foreach (JsonElement annotation in annotations.EnumerateArray())
                    {
                        if (annotation.TryGetProperty("url_citation", out JsonElement citation))
                        {
                            string title = citation.TryGetProperty("title", out JsonElement titleEl) ? titleEl.GetString() ?? "" : "";
                            string url = citation.TryGetProperty("url", out JsonElement urlEl) ? urlEl.GetString() ?? "" : "";
                            string snippet = citation.TryGetProperty("content", out JsonElement snippetEl) ? snippetEl.GetString() ?? "" : "";

                            StringBuilder entry = new StringBuilder();
                            entry.AppendLine($"{index}. {title}");
                            entry.AppendLine($"   URL: {url}");
                            if (!string.IsNullOrEmpty(snippet))
                            {
                                if (snippet.Length > 300)
                                {
                                    snippet = snippet.Substring(0, 300) + "...";
                                }

                                entry.AppendLine($"   {snippet}");
                            }

                            citations.Add(entry.ToString().TrimEnd());
                            index++;
                        }
                    }
                }

                break;
            }
        }

        // Return citations if available, otherwise fall back to the LLM summary.
        if (citations.Count > 0)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string c in citations)
            {
                sb.AppendLine(c);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine("---");
                sb.AppendLine(content);
            }

            return sb.ToString().TrimEnd();
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        return "No search results found.";
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
}
