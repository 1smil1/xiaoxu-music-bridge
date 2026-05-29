using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using xiaoxu_music_bridge.Media;

namespace xiaoxu_music_bridge.Tests;

[TestClass]
public sealed class BridgeApiTests
{
    [TestMethod]
    public async Task Health_ReturnsBridgeIdentity()
    {
        await using var factory = CreateFactory(new FakeMediaSessionService());
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.IsNotNull(response);
        Assert.IsTrue(response.Ok);
        Assert.AreEqual("xiaoxu-music-bridge", response.Name);
        Assert.AreEqual("0.1.0", response.Version);
    }

    [TestMethod]
    public async Task Status_ReturnsCurrentMediaStatus()
    {
        var status = new MediaStatus(
            Connected: true,
            Source: "QQMusic",
            Title: "云月谣",
            Artist: "兰音Reine",
            Album: "",
            CoverUrl: "http://127.0.0.1:17888/cover/current",
            IsPlaying: true,
            PositionMs: 63240,
            DurationMs: 245000,
            UpdatedAt: new DateTimeOffset(2026, 5, 29, 20, 30, 0, TimeSpan.FromHours(8)));
        await using var factory = CreateFactory(new FakeMediaSessionService { Status = status });
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<MediaStatus>("/status");

        Assert.IsNotNull(response);
        Assert.AreEqual("QQMusic", response.Source);
        Assert.AreEqual("云月谣", response.Title);
        Assert.AreEqual("兰音Reine", response.Artist);
        Assert.IsTrue(response.IsPlaying);
        Assert.AreEqual(63240, response.PositionMs);
        Assert.AreEqual(245000, response.DurationMs);
    }

    [TestMethod]
    [DataRow("/control/play-pause", ControlCommand.PlayPause)]
    [DataRow("/control/next", ControlCommand.Next)]
    [DataRow("/control/previous", ControlCommand.Previous)]
    public async Task ControlEndpoints_ForwardCommands(string path, ControlCommand expectedCommand)
    {
        var service = new FakeMediaSessionService();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(path, content: null);
        var body = await response.Content.ReadFromJsonAsync<ControlResponse>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(body);
        Assert.IsTrue(body.Ok);
        CollectionAssert.AreEqual(new[] { expectedCommand }, service.Commands);
    }

    [TestMethod]
    public async Task CoverCurrent_ReturnsNotFoundWhenNoCoverExists()
    {
        await using var factory = CreateFactory(new FakeMediaSessionService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/cover/current");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(IMediaSessionService mediaSessionService)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(mediaSessionService);
                });
            });
    }

    private sealed class FakeMediaSessionService : IMediaSessionService
    {
        public MediaStatus Status { get; init; } = MediaStatus.NoMedia();
        public List<ControlCommand> Commands { get; } = [];

        public Task<MediaStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Status);
        }

        public Task<ControlResult> SendCommandAsync(ControlCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new ControlResult(true));
        }

        public Task<CoverImage?> GetCurrentCoverAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<CoverImage?>(null);
        }
    }

    private sealed record HealthResponse(bool Ok, string Name, string Version);
    private sealed record ControlResponse(bool Ok);
}
