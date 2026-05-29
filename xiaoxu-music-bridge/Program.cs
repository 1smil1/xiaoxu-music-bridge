using xiaoxu_music_bridge.Media;

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

var app = builder.Build();

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

app.MapPost("/control/play-pause", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    ToHttpResult(await mediaSessionService.SendCommandAsync(ControlCommand.PlayPause, cancellationToken)));

app.MapPost("/control/next", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    ToHttpResult(await mediaSessionService.SendCommandAsync(ControlCommand.Next, cancellationToken)));

app.MapPost("/control/previous", async (IMediaSessionService mediaSessionService, CancellationToken cancellationToken) =>
    ToHttpResult(await mediaSessionService.SendCommandAsync(ControlCommand.Previous, cancellationToken)));

app.Run();

static IResult ToHttpResult(ControlResult result)
{
    return result.Ok
        ? Results.Ok(new ControlResponse(true))
        : Results.Problem(result.Error ?? "Media command failed", statusCode: StatusCodes.Status503ServiceUnavailable);
}

internal sealed record HealthResponse(bool Ok, string Name, string Version);

internal sealed record ControlResponse(bool Ok);

public partial class Program;
