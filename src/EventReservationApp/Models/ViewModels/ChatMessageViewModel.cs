namespace EventReservationApp.Models.ViewModels;

public class ChatMessageViewModel
{
    // "user" or "assistant" - matches the CSS classes / badges in Views/Chatbot/Index.cshtml
    public string Sender { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class ChatRequestViewModel
{
    public string Message { get; set; } = string.Empty;
}

public class ChatResponseViewModel
{
    public string Reply { get; set; } = string.Empty;

    // True when this message started a new conversation (after login or after the
    // previous one expired): the page then clears the old messages it still shows.
    public bool ConversationStarted { get; set; }
}
