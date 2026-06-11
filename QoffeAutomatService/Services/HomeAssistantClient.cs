using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QoffeAutomatService.Services
{
    public class HomeAssistantClient
    {
        private readonly HttpClient _http;
        private readonly HomeAssistantOptions _options;
        private readonly ILogger<HomeAssistantClient> _logger;

        public HomeAssistantClient(HttpClient http, IOptions<HomeAssistantOptions> options, ILogger<HomeAssistantClient> logger)
        {
            _http = http;
            _options = options.Value;
            _logger = logger;

            if (!string.IsNullOrEmpty(_options.Url))
            {
                _http.BaseAddress = new Uri(_options.Url);
            }

            // Authorization header set later for each request to allow token rotation
            // Default timeout can be set via HttpClientFactory configuration
        }

        private void EnsureAuthHeader()
        {
            if (!string.IsNullOrEmpty(_options.Token))
            {
                if (_http.DefaultRequestHeaders.Authorization == null || _http.DefaultRequestHeaders.Authorization.Parameter != _options.Token)
                {
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
                }
            }
            else
            {
                _http.DefaultRequestHeaders.Authorization = null;
            }
        }

        public async Task<HomeAssistantState?> GetEntityStateAsync(string entity, CancellationToken ct = default)
        {
            try
            {
                EnsureAuthHeader();
                using var resp = await _http.GetAsync($"/api/states/{entity}", ct);
                resp.EnsureSuccessStatusCode();
                var stream = await resp.Content.ReadAsStreamAsync(ct);

                var state = await JsonSerializer.DeserializeAsync<HomeAssistantState>(stream, HomeAssistantJsonContext.Default.HomeAssistantState, ct);
                return state;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching entity state {Entity}", entity);
                return null;
            }
        }
    }
}
