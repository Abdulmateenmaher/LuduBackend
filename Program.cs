using LuduBackend.Hubs;
using LuduBackend.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services
builder.Services.AddSignalR(options =>
{
    // Allow long-running hub method invocations (AI chains can take time).
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1 MB
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSingleton<RoomService>();

// 2. CORS policy — same-origin Netlify + localhost dev friendly.
builder.Services.AddCors(options =>
{
    options.AddPolicy("NetlifyFlutterPolicy", policy =>
    {
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // required for SignalR WebSockets
    });
});

// 3. Lightweight console request logging.
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
        options.UseUtcTimestamp = false;
    });
    logging.SetMinimumLevel(
        builder.Environment.IsDevelopment()
            ? LogLevel.Information
            : LogLevel.Warning);
});

var app = builder.Build();

// 4. WebSockets must be initialised BEFORE CORS and routing middleware
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

// 5. Apply the explicit CORS policy.
app.UseCors("NetlifyFlutterPolicy");

// 6. Endpoints
app.MapHub<GameHub>("/gamehub");
app.MapGet("/", () => Results.Json(new
{
    name = "Ludo Backend",
    status = "running",
    version = "1.0.0",
    timestamp = DateTime.UtcNow,
}));
app.MapGet("/health", () => Results.Json(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
}));

app.Logger.LogInformation("Ludo backend starting on {Urls}", string.Join(", ", app.Urls));

app.Run();
