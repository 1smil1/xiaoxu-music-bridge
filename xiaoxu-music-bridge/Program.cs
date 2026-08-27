using System.Net.Sockets;
using xiaoxu_music_bridge.Lyrics;
using xiaoxu_music_bridge.Media;
using xiaoxu_music_bridge.UI;

// ① Network connectivity check (TCP connect xiaoxu.xin:80, 5s timeout)
if (!await CheckNetworkConnectivityAsync())
{
    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm(networkError: true));
    return;
}

// ② Build Web Application (original logic)
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:17888");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(
                "http://xiaoxu.xin",
                "https://xiaoxu.xin",
                "http://localhost:5173",
                "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddSingleton<IMediaSessionService, WindowsMediaSessionService>();
builder.Services.AddHttpClient<LocalLyricService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("xiaoxu-music-bridge/0.1.0");
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
    {
        context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    await next();
});

app.UseCors();

app.MapGet("/health", () => new HealthResponse(true, "xiaoxu-music-bridge", "0.1.0"));

app.MapGet("/status", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    await mediaSessionService.GetStatusAsync(cancellationToken));

app.MapGet("/cover/current", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
{
    var cover = await mediaSessionService.GetCurrentCoverAsync(cancellationToken);
    return cover is null
        ? Results.NotFound()
        : Results.File(cover.Bytes, cover.ContentType);
});

app.MapGet("/lyrics/current", async (
    IMediaSessionService mediaSessionService,
    LocalLyricService lyricService,
    CancellationToken cancellationToken) =>
{
    var status = await mediaSessionService.GetStatusAsync(cancellationToken);
    return await lyricService.GetCurrentLyricsAsync(status, cancellationToken);
});

// Arbitrary-song lyrics lookup for listen-together guests. The host's
// LiveKit frames carry song title/artist/duration; the guest forwards them
// here, and the same LocalLyricService chain (local LRC → QQ Music →
// NetEase → lrclib) returns the result. The browser does not replicate the
// multi-source fallback in JS — it just consumes this endpoint on its own
// local bridge instance.
app.MapGet("/lyrics/query", async (
    string? title,
    string? artist,
    long? durationMs,
    LocalLyricService lyricService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(title))
    {
        return Results.BadRequest(new { error = "title is required" });
    }
    var status = new MediaStatus(
        Connected: true,
        Source: "listen-together-guest",
        Title: title,
        Artist: string.IsNullOrWhiteSpace(artist) ? null : artist,
        Album: null,
        CoverUrl: null,
        IsPlaying: false,
        PositionMs: 0,
        DurationMs: durationMs ?? 0,
        UpdatedAt: DateTimeOffset.Now);
    return Results.Ok(await lyricService.GetCurrentLyricsAsync(status, cancellationToken));
});

app.MapPost("/control/play-pause", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    ToHttpResult(await mediaSessionService.SendCommandAsync(ControlCommand.PlayPause, cancellationToken)));

app.MapPost("/control/next", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    ToHttpResult(await mediaSessionService.SendCommandAsync(ControlCommand.Next, cancellationToken)));

app.MapPost("/control/previous", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    ToHttpResult(await mediaSessionService.SendCommandAsync(ControlCommand.Previous, cancellationToken)));

// ③ Start Kestrel in background (non-blocking)
await app.StartAsync();

// ④ Start WinForms (blocks until user exits)
ApplicationConfiguration.Initialize();
Application.Run(new MainForm());

// ⑤ Graceful shutdown of web service
await app.StopAsync();

// Static helpers
static IResult ToHttpResult(ControlResult result)
{
    return result.Ok
        ? Results.Ok(new ControlResponse(true))
        : Results.Problem(result.Error ?? "Media command failed", statusCode: StatusCodes.Status503ServiceUnavailable);
}

static async Task<bool> CheckNetworkConnectivityAsync()
{
    try
    {
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync("xiaoxu.xin", 80);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
        var completedTask = await Task.WhenAny(connectTask, timeoutTask);
        return completedTask == connectTask && client.Connected;
    }
    catch
    {
        return false;
    }
}

internal sealed record HealthResponse(bool Ok, string Name, string Version);
internal sealed record ControlResponse(bool Ok);
public partial class Program;
