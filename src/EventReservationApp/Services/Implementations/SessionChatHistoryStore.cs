using System.Text.Json;
using EventReservationApp.Models.ViewModels;
using EventReservationApp.Services.Interfaces;

namespace EventReservationApp.Services.Implementations;

// Keeps the chat history in the ASP.NET Core session (server memory, keyed by the
// session cookie). The session is cleared on login and logout (Program.cs), so a
// new login never sees the previous conversation.
public class SessionChatHistoryStore : IChatHistoryStore
{
    private const string SessionKey = "Chatbot.Conversation";

    // Upper bound so one long chat cannot grow the session without limit;
    // the oldest messages are dropped first.
    private const int MaxMessages = 200;

    private readonly IHttpContextAccessor _httpContextAccessor;

    // Rasa starts a new session (forgets the context) this long after the last message
    // (session_expiration_time in the Rasa domain.yml). The shown history expires by
    // the same rule, so the user never sees messages Rasa no longer knows about.
    // Merely opening the chat page keeps the ASP.NET session alive, but not this.
    private readonly TimeSpan _conversationTimeout;

    public SessionChatHistoryStore(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _conversationTimeout = TimeSpan.FromMinutes(
            configuration.GetValue("Chatbot:SessionIdleTimeoutMinutes", 60));
    }

    private ISession Session =>
        _httpContextAccessor.HttpContext?.Session
        ?? throw new InvalidOperationException("Session is not available for this request.");

    public bool HasConversation(string userId) => Load(userId) is not null;

    public void StartConversation(string userId) =>
        Save(new StoredConversation { UserId = userId });

    public IReadOnlyList<ChatMessageViewModel> GetMessages(string userId) =>
        Load(userId)?.Messages ?? new List<ChatMessageViewModel>();

    public void Append(string userId, params ChatMessageViewModel[] messages)
    {
        var conversation = Load(userId) ?? new StoredConversation { UserId = userId };
        conversation.Messages.AddRange(messages);

        if (messages.Length > 0)
        {
            conversation.LastMessageUtc = messages[0].Timestamp;
        }

        if (conversation.Messages.Count > MaxMessages)
        {
            conversation.Messages.RemoveRange(0, conversation.Messages.Count - MaxMessages);
        }

        Save(conversation);
    }

    // A conversation stored for another user (e.g. a different account signed in
    // in the same browser without the session being cleared) is never returned.
    private StoredConversation? Load(string userId)
    {
        var json = Session.GetString(SessionKey);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var conversation = JsonSerializer.Deserialize<StoredConversation>(json);
        if (conversation?.UserId != userId)
        {
            return null;
        }

        if (conversation.LastMessageUtc is { } last &&
            DateTime.UtcNow - last > _conversationTimeout)
        {
            // Rasa has started a new session by now; drop the stale history too.
            Session.Remove(SessionKey);
            return null;
        }

        return conversation;
    }

    private void Save(StoredConversation conversation) =>
        Session.SetString(SessionKey, JsonSerializer.Serialize(conversation));

    private sealed class StoredConversation
    {
        public string UserId { get; set; } = string.Empty;
        public List<ChatMessageViewModel> Messages { get; set; } = new();

        // Null until the first message (a freshly started, still empty conversation).
        public DateTime? LastMessageUtc { get; set; }
    }
}
