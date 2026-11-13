
using System.Security.Claims;
using EventReservationApp.Models.ViewModels;
using EventReservationApp.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventReservationApp.Controllers;

[Authorize]
public class ChatbotController : Controller
{
    private readonly IChatbotConversationService _chatbotService;
    private readonly IChatHistoryStore _historyStore;
    private readonly ILogger<ChatbotController> _logger;

    public ChatbotController(
        IChatbotConversationService chatbotService,
        IChatHistoryStore historyStore,
        ILogger<ChatbotController> logger)
    {
        _chatbotService = chatbotService;
        _historyStore = historyStore;
        _logger = logger;
    }

    // GET: /Chatbot
    [HttpGet]
    public IActionResult Index()
    {
        return View();
    }

    // GET: /Chatbot/History
    // The whole conversation of the current session (see SessionChatHistoryStore);
    // empty after a new login.
    [HttpGet]
    public IActionResult History()
    {
        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        return Json(_historyStore.GetMessages(userId));
    }

    // POST: /Chatbot/Send
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(
        [FromBody] ChatRequestViewModel request,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new
            {
                error = "Message cannot be empty."
            });
        }

        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        // First message after login or after the conversation expired: Rasa may still
        // hold an older context (it keys conversations by user id), so start it fresh as well.
        var conversationStarted = !_historyStore.HasConversation(userId);
        if (conversationStarted)
        {
            try
            {
                await _chatbotService.StartNewConversationAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                // The message is still sent; at worst Rasa keeps the older context.
                _logger.LogWarning(ex, "Could not reset the Rasa conversation for user {UserId} after login.", userId);
            }

            _historyStore.StartConversation(userId);
        }

        var sentAt = DateTime.UtcNow;

        var response =
            await _chatbotService.GetReplyAsync(
                userId,
                request.Message,
                cancellationToken);

        _historyStore.Append(
            userId,
            new ChatMessageViewModel { Sender = "user", Text = request.Message, Timestamp = sentAt },
            new ChatMessageViewModel { Sender = "assistant", Text = response.Reply, Timestamp = DateTime.UtcNow });

        response.ConversationStarted = conversationStarted;
        return Json(response);
    }

    // POST: /Chatbot/NewConversation
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewConversation(
        CancellationToken cancellationToken)
    {
        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        await _chatbotService.StartNewConversationAsync(
            userId,
            cancellationToken);

        _historyStore.StartConversation(userId);

        return Ok();
    }
}
