using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Api;
using GEarth.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GEarth.Tests;

public sealed class RendererEndpointTests
{
    [Fact]
    public async Task ReadOnlyCrossOriginApiServesPinnedWeeksAllAgentsAndBoundedEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "renderer_http_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("http_renderer", false, 200);
            world.RendererCaptureEnabled = true;
            world.Revision = 1;
            await store.Save(world, 0, CancellationToken.None);
            var openingSeq = world.Seq;
            for (var day = 0; day < 7; day++) SocietyEngine.Advance(world);
            world.Revision = 2;
            await store.Save(world, 1, CancellationToken.None);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IWorldStore>(store);
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
            builder.Services.AddCors(options => options.AddPolicy("RendererReadOnly", policy => policy.AllowAnyOrigin().WithMethods("GET").AllowAnyHeader()));
            await using var app = builder.Build();
            app.UseCors();
            app.MapRenderer();
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("Origin", "https://kennys-renderer.example");
            const string root = "/api/runs/http_renderer/renderer";
            var response = await client.GetAsync(root + "/manifest");
            Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            var manifest = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(RendererReplay.Contract, manifest["schema_version"]!.GetValue<string>());
            Assert.True(manifest["captured_from_opening"]!.GetValue<bool>());
            var page = (await client.GetFromJsonAsync<JsonObject>(root + "/frames?limit=1"))!;
            Assert.True(page["has_more"]!.GetValue<bool>());
            Assert.Single(page["frames"]!.AsArray());
            var next = (await client.GetFromJsonAsync<JsonObject>(root + "/frames?after_revision=1"))!;
            Assert.Equal(7, Assert.Single(next["frames"]!.AsArray())!["tick"]!.GetValue<int>());
            var opening = (await client.GetFromJsonAsync<JsonObject>(root + "/frames/1"))!;
            Assert.Equal(0, opening["tick"]!.GetValue<int>());
            Assert.Equal(200, opening["snapshot"]!["agents"]!.AsArray().Count);
            Assert.NotNull(opening["snapshot"]!["banks"]![0]!["state"]!["reserves_minor"]);
            var events = (await client.GetFromJsonAsync<JsonObject>(root + $"/events?through_seq={openingSeq}&limit=2"))!;
            Assert.All(events["events"]!.AsArray(), item => Assert.InRange(item!["seq"]!.GetValue<long>(), 1, openingSeq));
            Assert.InRange(events["events"]!.AsArray().Count, 1, 2);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/frames/999")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync(root + "/frames", new { })).StatusCode);
            await app.StopAsync();
        }
        finally { Directory.Delete(directory, true); }
    }
}
