using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Faultline.Api;
using Faultline.Core;

namespace Faultline.Tests;

public sealed class WorkerClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => respond(request, token);
    }

    private static (World World, DecisionJob Job) CreateJob()
    {
        var world = SocietyFactory.Create("worker_metrics", true, 160);
        return (world, SocietyEngine.Observe(world, "aster_citizen_00001", DateTimeOffset.UtcNow));
    }

    private static DecisionResult ModelResult(DecisionJob job) => new(job.RequestId,
        new("hold", new JsonObject(), "Wait.", []), "model", "test-model", 7);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidWorkerResponseRetainsTimingThroughEngineAcceptanceOrRejection(bool expired)
    {
        var (world, job) = CreateJob();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var received = await request.Content!.ReadFromJsonAsync<DecisionJob>(Wire.Json, token);
            Assert.Equal(job.RequestId, received!.RequestId);
            await Task.Delay(10, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ModelResult(job), options: Wire.Json) };
        }));
        var result = await WorkerClient.Decide(http, job, true, CancellationToken.None);
        Assert.Equal(job.RequestId, result.RequestId);
        Assert.Equal(7, result.LatencyMs);
        Assert.True(result.CoordinatorLatencyMs >= 5);
        var record = SocietyEngine.Apply(world, job, result, expired ? job.Deadline.AddSeconds(1) : DateTimeOffset.UtcNow);
        Assert.Equal(!expired, record.Accepted);
        Assert.Equal(expired ? "EXPIRED_PROPOSAL" : "OK", record.Code);
        var payload = world.Events.Last().Payload;
        Assert.Equal(job.RequestId, payload["decision_id"]!.GetValue<string>());
        Assert.Equal(job.RequestId, payload["result_request_id"]!.GetValue<string>());
        Assert.Equal(result.CoordinatorLatencyMs, payload["coordinator_latency_ms"]!.GetValue<double>());
        Assert.Equal(7, payload["latency_ms"]!.GetValue<double>());
        Assert.Equal(expired ? 0 : 1, world.Society!.ModelDecisionCount);
        Assert.Equal(0, world.Society.FallbackDecisionCount);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task WorkerHttpRejectionPreservesStatusAndMeasuredDuration(int status)
    {
        var (world, job) = CreateJob();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(10, token);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private remote response") };
        }));
        var result = await WorkerClient.Decide(http, job, true, CancellationToken.None);
        Assert.Equal("rule_fallback", result.Source);
        Assert.Equal("worker_http_error", result.ErrorCategory);
        Assert.Equal(status, result.ErrorHttpStatus);
        Assert.True(result.CoordinatorLatencyMs >= 5);
        Assert.Equal(0, result.LatencyMs); // No model timing was returned by the worker.
        Assert.DoesNotContain("private", result.Error!);
        SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);
        Assert.Equal(status, world.Events.Last().Payload["error_http_status"]!.GetValue<int>());
        Assert.Equal(0, world.Society!.ModelDecisionCount);
        Assert.Equal(1, world.Society.FallbackDecisionCount);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task MalformedWorkerJsonIsClassifiedWithCoordinatorTiming(string body)
    {
        var (_, job) = CreateJob();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") })));
        var result = await WorkerClient.Decide(http, job, true, CancellationToken.None);
        Assert.Equal(job.RequestId, result.RequestId);
        Assert.Equal("rule_fallback", result.Source);
        Assert.Equal("worker_invalid_response", result.ErrorCategory);
        Assert.True(result.CoordinatorLatencyMs > 0);
    }

    [Theory]
    [InlineData("wrong_id")]
    [InlineData("wrong_source")]
    [InlineData("negative_duration")]
    [InlineData("model_with_error")]
    public async Task InvalidWorkerEnvelopeCannotMasqueradeAsAValidModelResponse(string fault)
    {
        var (_, job) = CreateJob();
        var response = ModelResult(job);
        response = fault switch
        {
            "wrong_id" => response with { RequestId = "other_request" },
            "wrong_source" => response with { Source = "rule" },
            "model_with_error" => response with { Error = "TimeoutError" },
            _ => response with { LatencyMs = -1 }
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(response, options: Wire.Json) })));
        var result = await WorkerClient.Decide(http, job, true, CancellationToken.None);
        Assert.Equal(job.RequestId, result.RequestId);
        Assert.Equal("worker_invalid_response", result.ErrorCategory);
        Assert.Equal("rule_fallback", result.Source);
    }

    [Fact]
    public async Task HttpClientDeadlineIsATimedFallbackButHostCancellationPropagates()
    {
        var (_, job) = CreateJob();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        })) { Timeout = TimeSpan.FromMilliseconds(30) };
        var timeout = await WorkerClient.Decide(http, job, true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("worker_timeout", timeout.ErrorCategory);
        Assert.Equal("rule_fallback", timeout.Source);
        Assert.True(timeout.CoordinatorLatencyMs >= 10);
        using var stop = new CancellationTokenSource();
        var request = WorkerClient.Decide(http, job, true, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task TransportFailureHasMeasuredDurationAndDoesNotClaimModelLatency()
    {
        var (_, job) = CreateJob();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(10, token);
            throw new HttpRequestException("private transport details");
        }));
        var result = await WorkerClient.Decide(http, job, true, CancellationToken.None);
        Assert.Equal("worker_transport_error", result.ErrorCategory);
        Assert.Null(result.ErrorHttpStatus);
        Assert.True(result.CoordinatorLatencyMs >= 5);
        Assert.Equal(0, result.LatencyMs);
    }
}
