using LuduBackend.Hubs;
using LuduBackend.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Add services
builder.Services.AddSignalR();
builder.Services.AddSingleton<RoomService>();

// 2. Fix CORS policy to support your exact Netlify sub-domain dynamically
builder.Services.AddCors(options =>
{
    options.AddPolicy("NetlifyFlutterPolicy", policy =>
    {
        policy.SetIsOriginAllowed(origin => true) // Allows localhost and https://ludobackend.netlify.app automatically
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // ⚠️ Crucial for SignalR WebSockets handshake
    });
});

var app = builder.Build();

// 3. WebSockets must be initialized BEFORE CORS and routing middleware
app.UseWebSockets();

// 4. Apply the explicit policy name here
app.UseCors("NetlifyFlutterPolicy");

// 5. Map your endpoints
app.MapHub<GameHub>("/gamehub");
app.MapGet("/", () => "Ludo Backend Running");

app.Run();
// run
