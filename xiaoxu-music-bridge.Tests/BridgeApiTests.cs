using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task CorsPreflight_AllowsPrivateNetworkAccess()
    {
        await using var factory = CreateFactory(new FakeMediaSessionService());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/status");
        request.Headers.Add("Origin", "http://xiaoxu.xin");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("Access-Control-Allow-Private-Network", out var values));
        CollectionAssert.Contains(values.ToArray(), "true");
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

    [TestMethod]
    public async Task LyricsCurrent_ReturnsMatchingLocalLrc()
    {
        var lyricsDirectory = Path.Combine(Path.GetTempPath(), $"xiaoxu-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lyricsDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(lyricsDirectory, "兰音Reine - 云月谣.lrc"), "[00:01.00]星河落下\n[00:03.00]梦在发光");
            var status = new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: "云月谣",
                Artist: "兰音Reine",
                Album: "",
                CoverUrl: null,
                IsPlaying: true,
                PositionMs: 0,
                DurationMs: 120000,
                UpdatedAt: DateTimeOffset.Now);
            await using var factory = CreateFactory(new FakeMediaSessionService { Status = status }, lyricsDirectory);
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<LyricResponse>("/lyrics/current");

            Assert.IsNotNull(response);
            Assert.IsTrue(response.Found);
            Assert.AreEqual("兰音Reine - 云月谣.lrc", response.FileName);
            Assert.AreEqual("local", response.Source);
            Assert.IsTrue(response.Synced);
            StringAssert.Contains(response.Lrc, "星河落下");
        }
        finally
        {
            Directory.Delete(lyricsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LyricsCurrent_FetchesSyncedLyricsFromLrclibWhenLocalFileIsMissing()
    {
        var lyricsDirectory = Path.Combine(Path.GetTempPath(), $"xiaoxu-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lyricsDirectory);
        try
        {
            var status = new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: "See You in Life",
                Artist: "Valentina Ploy",
                Album: "Satellite",
                CoverUrl: null,
                IsPlaying: true,
                PositionMs: 0,
                DurationMs: 241749,
                UpdatedAt: DateTimeOffset.Now);
            var lyricsPayload = JsonSerializer.Serialize(new[]
            {
                new
                {
                    trackName = "See You in Life",
                    artistName = "Valentina Ploy",
                    albumName = "Satellite",
                    duration = 242,
                    plainLyrics = "See you in life",
                    syncedLyrics = "[00:01.00]See you in life\n[00:03.00]Under the satellite"
                }
            });
            await using var factory = CreateFactory(
                new FakeMediaSessionService { Status = status },
                lyricsDirectory,
                new FakeHttpMessageHandler(lyricsPayload));
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<LyricResponse>("/lyrics/current");

            Assert.IsNotNull(response);
            Assert.IsTrue(response.Found);
            Assert.AreEqual("lrclib-synced", response.Source);
            Assert.IsTrue(response.Synced);
            StringAssert.Contains(response.Lrc, "[00:01.00]See you in life");
        }
        finally
        {
            Directory.Delete(lyricsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LyricsCurrent_FallsBackToNeteaseForChineseLyrics()
    {
        var lyricsDirectory = Path.Combine(Path.GetTempPath(), $"xiaoxu-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lyricsDirectory);
        try
        {
            var status = new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: "虚情假意",
                Artist: "蓝心羽",
                Album: "虚情假意",
                CoverUrl: null,
                IsPlaying: true,
                PositionMs: 0,
                DurationMs: 188223,
                UpdatedAt: DateTimeOffset.Now);
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url.Contains("lrclib.net"))
                {
                    return "[]";
                }

                if (url.Contains("/api/search/get/web"))
                {
                    return JsonSerializer.Serialize(new
                    {
                        result = new
                        {
                            songs = new[]
                            {
                                new
                                {
                                    id = 1954914405,
                                    name = "虚情假意 (伴奏)",
                                    artists = new[] { new { name = "蓝心羽" } },
                                    duration = 188000
                                },
                                new
                                {
                                    id = 1954914404,
                                    name = "虚情假意",
                                    artists = new[] { new { name = "蓝心羽" } },
                                    duration = 188000
                                }
                            }
                        }
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    lrc = new
                    {
                        lyric = "[00:04.47]怀念那时的我们 纯真无邪的脸\n[00:08.44]怀念天真的过去 说要到永远"
                    }
                });
            });
            await using var factory = CreateFactory(new FakeMediaSessionService { Status = status }, lyricsDirectory, handler);
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<LyricResponse>("/lyrics/current");

            Assert.IsNotNull(response);
            Assert.IsTrue(response.Found);
            Assert.AreEqual("netease", response.Source);
            Assert.IsTrue(response.Synced);
            Assert.AreEqual("蓝心羽 - 虚情假意", response.FileName);
            StringAssert.Contains(response.Lrc, "怀念那时的我们");
        }
        finally
        {
            Directory.Delete(lyricsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LyricsCurrent_DoesNotReturnLowScoreNeteaseMatch()
    {
        var lyricsDirectory = Path.Combine(Path.GetTempPath(), $"xiaoxu-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lyricsDirectory);
        try
        {
            var status = new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: "太迟 (1个球版)",
                Artist: "1个球",
                Album: "太迟 (1个球版)",
                CoverUrl: null,
                IsPlaying: true,
                PositionMs: 0,
                DurationMs: 179494,
                UpdatedAt: DateTimeOffset.Now);
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url.Contains("lrclib.net") || url.Contains("/api/search?"))
                {
                    return "[]";
                }

                if (url.Contains("/api/search/get/web"))
                {
                    return JsonSerializer.Serialize(new
                    {
                        result = new
                        {
                            songs = new[]
                            {
                                new
                                {
                                    id = 1,
                                    name = "咱们结婚吧",
                                    artists = new[] { new { name = "1个球" } },
                                    duration = 179000
                                }
                            }
                        }
                    });
                }

                return JsonSerializer.Serialize(new { lrc = new { lyric = "[00:00.00]错误歌词" } });
            });
            await using var factory = CreateFactory(new FakeMediaSessionService { Status = status }, lyricsDirectory, handler);
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<LyricResponse>("/lyrics/current");

            Assert.IsNotNull(response);
            Assert.IsFalse(response.Found);
        }
        finally
        {
            Directory.Delete(lyricsDirectory, recursive: true);
        }
    }


    [TestMethod]
    public async Task LyricsCurrent_UsesDirectQqMusicLyricsFirst()
    {
        var lyricsDirectory = Path.Combine(Path.GetTempPath(), $"xiaoxu-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lyricsDirectory);
        try
        {
            var status = new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: "星降る海 (Starry Sea)",
                Artist: "Aqu3ra/早見沙織 (はやみ さおり)",
                Album: "超かぐや姫！",
                CoverUrl: null,
                IsPlaying: true,
                PositionMs: 0,
                DurationMs: 253441,
                UpdatedAt: DateTimeOffset.Now);
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url.Contains("client_search_cp"))
                {
                    return JsonSerializer.Serialize(new
                    {
                        data = new
                        {
                            song = new
                            {
                                list = new[]
                                {
                                    new
                                    {
                                        songmid = "001AdB7w12o1XY",
                                        songname = "星降る海 (Starry Sea)",
                                        singer = new[] { new { name = "Aqu3ra" }, new { name = "早見沙織" } },
                                        interval = 253
                                    }
                                }
                            }
                        }
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    retcode = 0,
                    code = 0,
                    lyric = "[00:01.37]幾千の時を巡って今\n[00:07.25]僕ら出会えたの"
                });
            });
            await using var factory = CreateFactory(new FakeMediaSessionService { Status = status }, lyricsDirectory, handler);
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<LyricResponse>("/lyrics/current");

            Assert.IsNotNull(response);
            Assert.IsTrue(response.Found);
            Assert.AreEqual("qqmusic-direct", response.Source);
            Assert.IsTrue(response.Synced);
            Assert.AreEqual("Aqu3ra/早見沙織 - 星降る海 (Starry Sea)", response.FileName);
            StringAssert.Contains(response.Lrc, "幾千の時");
        }
        finally
        {
            Directory.Delete(lyricsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LyricsCurrent_UsesQqMusicBeforeOtherOnlineSources()
    {
        var lyricsDirectory = Path.Combine(Path.GetTempPath(), $"xiaoxu-lyrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lyricsDirectory);
        try
        {
            var status = new MediaStatus(
                Connected: true,
                Source: "QQMusic",
                Title: "半抹烟熏",
                Artist: "封茗囧菌",
                Album: "半抹烟熏",
                CoverUrl: null,
                IsPlaying: true,
                PositionMs: 0,
                DurationMs: 223914,
                UpdatedAt: DateTimeOffset.Now);
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url.Contains("/api/search"))
                {
                    return JsonSerializer.Serialize(new
                    {
                        data = new
                        {
                            list = new[]
                            {
                                new
                                {
                                    mid = "001abc",
                                    title = "半抹烟熏",
                                    singer = new[] { new { name = "封茗囧菌" } },
                                    interval = 224
                                }
                            }
                        }
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        lyric = "[00:17.10]路人四下张望 谁丢的姑娘\n[00:20.54]化了半边的妆"
                    }
                });
            });
            await using var factory = CreateFactory(new FakeMediaSessionService { Status = status }, lyricsDirectory, handler);
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<LyricResponse>("/lyrics/current");

            Assert.IsNotNull(response);
            Assert.IsTrue(response.Found);
            Assert.AreEqual("qqmusic", response.Source);
            Assert.IsTrue(response.Synced);
            Assert.AreEqual("封茗囧菌 - 半抹烟熏", response.FileName);
            StringAssert.Contains(response.Lrc, "路人四下张望");
        }
        finally
        {
            Directory.Delete(lyricsDirectory, recursive: true);
        }
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

    private static WebApplicationFactory<Program> CreateFactory(IMediaSessionService mediaSessionService, string lyricsDirectory)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Lyrics:Directory", lyricsDirectory);
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(mediaSessionService);
                });
            });
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IMediaSessionService mediaSessionService,
        string lyricsDirectory,
        HttpMessageHandler lyricsHttpMessageHandler)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Lyrics:Directory", lyricsDirectory);
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(mediaSessionService);
                    services.RemoveAll<HttpClient>();
                    services.AddHttpClient<xiaoxu_music_bridge.Lyrics.LocalLyricService>()
                        .ConfigurePrimaryHttpMessageHandler(() => lyricsHttpMessageHandler);
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
    private sealed record LyricResponse(bool Found, string? Title, string? Artist, string? FileName, string? Lrc, string? Source, bool Synced);

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _responseFactory;

        public FakeHttpMessageHandler(string responseBody)
            : this(_ => responseBody)
        {
        }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, string> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseFactory(request), Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
