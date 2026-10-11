using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Faultline.Core;

namespace Faultline.Api;

public static class WorkerClient
{
    public static async Task<DecisionResult> Decide(HttpClient worker, DecisionJob job, bool live,
        CancellationToken cancellationToken)
    {
        if (!live)
        {
            var stageTwo = job.Observation["world_stage"]?.GetValue<int>() == 2;
            var choice = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{job.AgentId}:{job.ObservationTick}"))[0] % job.Candidates.Length;
            var candidate = stageTwo ? job.Candidates[choice]
                : job.Candidates.FirstOrDefault(item => item.ActionType is "purchase" or "choose_route") ?? job.Candidates[0];
            return new DecisionResult(job.RequestId,
                new ActionProposal(candidate.ActionType, (JsonObject)candidate.Params.DeepClone(),
                    "Deterministic rule choice; no model inference.", []), "rule", "none", 0);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await worker.PostAsJsonAsync("http://127.0.0.1:8100/decide", job,
                Wire.Json, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<DecisionResult>(Wire.Json, cancellationToken);
            if (result is null || result.RequestId != job.RequestId
                || result.Source is not ("model" or "rule_fallback")
                || result.Action is null || result.Action.Params is null
                || string.IsNullOrWhiteSpace(result.Action.Reason) || result.Action.EvidenceIds is null
                || result.Source == "model" && (result.Error is not null
                    || result.ErrorCategory is not null || result.ErrorHttpStatus is not null)
                || !double.IsFinite(result.LatencyMs) || result.LatencyMs < 0)
                throw new InvalidOperationException("Invalid worker response");
            return result with { CoordinatorLatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown abandons this request; it must not become an applied fallback.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            or JsonException or InvalidOperationException)
        {
            var category = exception switch
            {
                OperationCanceledException => "worker_timeout",
                HttpRequestException { StatusCode: not null } => "worker_http_error",
                HttpRequestException => "worker_transport_error",
                _ => "worker_invalid_response"
            };
            var status = exception is HttpRequestException { StatusCode: { } code } ? (int?)code : null;
            return new DecisionResult(job.RequestId,
                new ActionProposal("hold", new JsonObject(), "Inference unavailable; explicit fallback hold.", []),
                "rule_fallback", "none", 0, exception.GetType().Name,
                CoordinatorLatencyMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ErrorCategory: category, ErrorHttpStatus: status);
        }
    }
}
