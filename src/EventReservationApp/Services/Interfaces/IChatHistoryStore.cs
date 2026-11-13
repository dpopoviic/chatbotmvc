using EventReservationApp.Models.ViewModels;

namespace EventReservationApp.Services.Interfaces;

// Chat history shown on the chatbot page. It lives only in the user's ASP.NET Core
// session: it lasts until logout, browser close or session idle timeout, and the next
// login starts empty. Nothing is written to a database.
public interface IChatHistoryStore
{
    // True once a conversation was started for this user in the current session
    // (first message or "new conversation"); false right after login.
    bool HasConversation(string userId);

    // Starts an empty conversation for this user in the current session.
    void StartConversation(string userId);

    // Messages of the current conversation, oldest first.
    IReadOnlyList<ChatMessageViewModel> GetMessages(string userId);

    void Append(string userId, params ChatMessageViewModel[] messages);
}
