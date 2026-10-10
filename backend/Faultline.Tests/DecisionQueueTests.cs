using Faultline.Api;
using Faultline.Core;
using System.Text.Json.Nodes;

namespace Faultline.Tests;

public sealed class DecisionQueueTests
{
    private static DecisionJob Job(string agent) => new(agent + "_request", "queue_test", agent, 0,
        DateTimeOffset.UtcNow.AddSeconds(60), new(), [], new JsonObject(), []);

    private static DecisionResult Result(DecisionJob job) => new(job.RequestId,
        new("hold", new JsonObject(), "Test choice.", []), "model", "test", 1);

    [Fact]
    public async Task AdmissionRejectsDuplicatesAndExcessWorkBeforeCallingWorker()
    {
        await using var queue = new DecisionQueue(2, CancellationToken.None);
        var completion = new TaskCompletionSource<DecisionResult>();
        var calls = 0;
        Task<DecisionResult> Decide(DecisionJob job, CancellationToken token)
        {
            calls++;
            return completion.Task.WaitAsync(token);
        }
        Assert.True(queue.TryStart(Job("first"), Decide));
        Assert.False(queue.TryStart(Job("first"), Decide));
        Assert.True(queue.TryStart(Job("second"), Decide));
        Assert.False(queue.TryStart(Job("third"), Decide));
        Assert.Equal(2, calls);
        Assert.Equal(0, queue.FreeSlots);
        Assert.Equal(2, await queue.CancelAndDrain());
        Assert.Empty(queue.AgentIds);
        Assert.Equal(2, queue.FreeSlots);
        Assert.False(queue.TryStart(Job("after_stop"), Decide));
    }

    [Fact]
    public async Task SlowRequestDoesNotBlockCompletedResultsOrRefillingFreeSlots()
    {
        await using var queue = new DecisionQueue(2, CancellationToken.None);
        var slow = new TaskCompletionSource<DecisionResult>();
        Assert.True(queue.TryStart(Job("slow"), (_, token) => slow.Task.WaitAsync(token)));
        Assert.True(queue.TryStart(Job("fast"), (job, _) => Task.FromResult(Result(job))));
        await queue.WaitForCompletion(CancellationToken.None);
        var completed = Assert.Single(queue.TakeCompleted());
        Assert.Equal("fast", completed.Job.AgentId);
        Assert.Equal("fast_request", (await completed.Task).RequestId);
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryStart(Job("replacement"), (job, _) => Task.FromResult(Result(job))));
        Assert.Equal("replacement", Assert.Single(queue.TakeCompleted()).Job.AgentId);
        Assert.Contains("slow", queue.AgentIds);
    }

    [Fact]
    public async Task FinalDrainWaitCanBeCanceledAndFaultedRequestsAreRemoved()
    {
        using var stop = new CancellationTokenSource();
        await using var queue = new DecisionQueue(2, CancellationToken.None);
        var slow = new TaskCompletionSource<DecisionResult>();
        queue.TryStart(Job("slow"), (_, token) => slow.Task.WaitAsync(token));
        var waiting = queue.WaitForCompletion(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, await queue.CancelAndDrain());
        Assert.Equal(0, queue.Count);

        await using var faulted = new DecisionQueue(1, CancellationToken.None);
        faulted.TryStart(Job("fault"), (_, _) => Task.FromException<DecisionResult>(new InvalidOperationException()));
        var request = Assert.Single(faulted.TakeCompleted());
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.Task);
        Assert.Equal(1, faulted.FreeSlots);
    }
}
