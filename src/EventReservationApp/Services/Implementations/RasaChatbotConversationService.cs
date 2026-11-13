using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventReservationApp.Models.Entities;
using EventReservationApp.Models.ViewModels;
using EventReservationApp.Services.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace EventReservationApp.Services.Implementations;


public class RasaChatbotConversationService : IChatbotConversationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<RasaChatbotConversationService> _logger;
    private readonly IUserTokenService _userTokenService;
    private readonly UserManager<ApplicationUser> _userManager;

    public RasaChatbotConversationService(
        HttpClient httpClient,
        IConfiguration configuration,
        IUserTokenService userTokenService,
        UserManager<ApplicationUser> userManager,
        ILogger<RasaChatbotConversationService> logger)
    {
        _logger = logger;
        _userTokenService = userTokenService;
        _userManager = userManager;

        var baseUrl = configuration["RasaSettings:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("RasaSettings:BaseUrl is not configured.");
        }

        httpClient.BaseAddress = new Uri(baseUrl);

        _httpClient = httpClient;
    }

    // Every call to Rasa goes through the secure_rest channel (secure_rest_channel.py
    // in the Rasa project) with a signed user token: it proves the request comes
    // from this app for this user, and Rasa rejects it unless the token's subject
    // equals the sender in the body.
    // The correlation id goes into the token (claim "cid"), so it reaches Rasa,
    // its actions, the MCP tools and the internal API signed and unchanged.
    private async Task<HttpRequestMessage> CreateSecureRestRequestAsync(
        string path,
        string userId,
        object body,
        string correlationId)
    {
        var user = await _userManager.FindByIdAsync(userId)
            ?? throw new InvalidOperationException($"User {userId} not found.");
        var roles = await _userManager.GetRolesAsync(user);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _userTokenService.CreateToken(userId, roles, correlationId));
        return request;
    }

    // One correlation id per chat message (or restart). Every log written inside
    // the returned scope - here and in anything called from here - carries it.
    private (string CorrelationId, IDisposable? LogScope) BeginCorrelation()
    {
        var correlationId = Guid.NewGuid().ToString();
        var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        return (correlationId, scope);
    }

    public async Task<ChatResponseViewModel> GetReplyAsync(
        string userId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var (correlationId, logScope) = BeginCorrelation();
        using var _ = logScope;
        try
        {
            var payload = new RasaWebhookRequest
            {
                Sender = userId,
                Message = message,
                Metadata = new() { ["language"] = "sr-Cyrl" }
            };

            using var request = await CreateSecureRestRequestAsync(
                "webhooks/secure_rest/webhook", userId, payload, correlationId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var botMessages = await response.Content.ReadFromJsonAsync<List<RasaBotMessage>>(
                JsonOptions, cancellationToken) ?? new List<RasaBotMessage>();

            // Every reply must be addressed to the user who sent the message;
            // anything else is dropped instead of being shown to this user.
            var foreignCount = botMessages.Count(m => m.RecipientId != userId);
            if (foreignCount > 0)
            {
                _logger.LogWarning(
                    "Dropped {Count} Rasa message(s) not addressed to user {UserId}.", foreignCount, userId);
            }

            var reply = string.Join(
                "\n\n",
                botMessages
                    .Where(m => m.RecipientId == userId)
                    .Select(m => m.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            return new ChatResponseViewModel
            {
                Reply = string.IsNullOrWhiteSpace(reply)
                    ? "Žao mi je, nisam siguran kako da odgovorim na to. Možete li pokušati drugačije?"
                    : reply
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rasa chatbot call failed for user {UserId}.", userId);
            return new ChatResponseViewModel
            {
                Reply = "Došlo je do problema prilikom komunikacije sa chatbotom. " +
                        "Molimo pokušajte ponovo kasnije."
            };
        }
    }

    public async Task StartNewConversationAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var (correlationId, logScope) = BeginCorrelation();
        using var _ = logScope;
        try
        {
            // Restart route of the secure_rest channel: same token check as a
            // message, so Rasa runs without its HTTP API (--enable-api) and auth token.
            using var request = await CreateSecureRestRequestAsync(
                "webhooks/secure_rest/restart", userId, new { sender = userId }, correlationId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Rasa tracker for user {UserId}.", userId);
            throw;
        }
    }

    private sealed class RasaWebhookRequest
    {
        public required string Sender { get; init; }
        public required string Message { get; init; }
        public Dictionary<string, string>? Metadata { get; init; }
    }

    private sealed class RasaBotMessage
    {
        [JsonPropertyName("recipient_id")]
        public string? RecipientId { get; init; }

        public string? Text { get; init; }
    }
}
