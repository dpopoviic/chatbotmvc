using EventReservationApp.Data;
using EventReservationApp.Models.Entities;
using EventReservationApp.Services.Auth;
using EventReservationApp.Services.Implementations;
using EventReservationApp.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------
// Database
// ---------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found in appsettings.json.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

// ---------------------------------------------------------------------
// ASP.NET Core Identity
// ---------------------------------------------------------------------
// Uses the default Identity UI (scaffolded Razor Pages shipped inside the
// Microsoft.AspNetCore.Identity.UI package) for Register/Login/Logout/etc.
// Run `dotnet aspnet-codegenerator identity` if you ever want to scaffold
// and customize the physical Identity Razor Pages into this project.
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        // Standard password/account options - adjust as needed.
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 6;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";

    // Chat history lives in the session (SessionChatHistoryStore): every login and
    // logout starts a clean session, so the next login never sees the old chat.
    options.Events.OnSignedIn = context =>
    {
        context.HttpContext.Session.Clear();
        return Task.CompletedTask;
    };
    options.Events.OnSigningOut = context =>
    {
        context.HttpContext.Session.Clear();
        return Task.CompletedTask;
    };
});

// ---------------------------------------------------------------------
// Session - holds the chatbot history of the current login (server memory).
// The session cookie has no expiry date, so it is gone when the browser
// closes; the data is also dropped after IdleTimeout without requests.
// Keep IdleTimeout in line with session_expiration_time in the Rasa
// domain.yml (60 min) - after that Rasa forgets the context as well.
// AddDistributedMemoryCache = one server; with several servers use a shared
// cache (e.g. Redis) instead.
// ---------------------------------------------------------------------
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(
        builder.Configuration.GetValue("Chatbot:SessionIdleTimeoutMinutes", 60));
    options.Cookie.Name = ".EventReservationApp.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// ---------------------------------------------------------------------
// Internal API key auth scheme - used only by the Rasa action server to
// call the internal agent API on behalf of a signed-in user. Separate from
// the Identity cookie scheme; the internal controller opts into it
// explicitly via [Authorize(AuthenticationSchemes = "InternalApiKey")].
// See Services/Auth/InternalApiKeyHandler.cs.
// ---------------------------------------------------------------------
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, InternalApiKeyHandler>(
        InternalApiKeyHandler.SchemeName, _ => { });

// Signs the per-message user token sent to Rasa (and forwarded back by its
// custom actions), and validates it in InternalApiKeyHandler.
builder.Services.AddSingleton<IUserTokenService, UserTokenService>();

// ---------------------------------------------------------------------
// Application services (business logic lives here, not in controllers)
// ---------------------------------------------------------------------
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<IReservationService, ReservationService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
builder.Services.AddScoped<IRoleManagementService, RoleManagementService>();


builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddScoped<IEventCatalogService, EventCatalogService>();
builder.Services.AddScoped<IMyReservationsService, MyReservationsService>();
// Runs a reservation/cancel from the Rasa actions at most once per Idempotency-Key.
builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
// Chatbot history of the current session (no database).
builder.Services.AddScoped<IChatHistoryStore, SessionChatHistoryStore>();

// Chatbot service is intentionally isolated behind an interface so the AI
// provider can be swapped without touching the controller or views. See
// Services/Interfaces/IChatbotConverationService.cs.
//
// RasaChatbotConversationService talks to a Rasa Pro server (REST channel +
// tracker API) instead of an in-process agent; the actual tool logic
// (search events, availability, reservations) is exposed to Rasa's custom
// actions via the internal API below, not called in-process anymore.
// The ReAct sub agent can make several LLM and tool calls for one message,
// so the default 100 s HttpClient timeout is too tight.
builder.Services.AddHttpClient<IChatbotConversationService, RasaChatbotConversationService>(
    client => client.Timeout = TimeSpan.FromSeconds(180));
// ---------------------------------------------------------------------
// MVC + Razor Pages (Razor Pages are required by the default Identity UI)
// ---------------------------------------------------------------------
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// The chatbot page posts messages via fetch(); configure the antiforgery
// header name so its JavaScript can send the token explicitly (see
// Views/Chatbot/Index.cshtml).
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

var app = builder.Build();

// ---------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Before authentication: sign-in/sign-out events clear the session.
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

// ---------------------------------------------------------------------
// Seed database (roles, example users, events, reservations)
// ---------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    await DbInitializer.SeedAsync(scope.ServiceProvider);
}

app.Run();
