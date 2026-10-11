using Faultline.Core;

namespace Faultline.Api;

public sealed record PendingDecision(DecisionJob Job, Task<DecisionResult> Task);

public sealed class DecisionQueue(int capacity, CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly CancellationTokenSource requests = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    // Ordered so completed decisions apply in dispatch order; a Dictionary reuses freed slots, so its order
    // depends on past removals and a resumed fork would apply the same decisions in a different order.
    private readonly OrderedDictionary<string, PendingDecision> pending = new();

    public int Count => pending.Count;
    public int FreeSlots => Math.Max(0, capacity - Count);
    public IReadOnlySet<string> AgentIds => pending.Keys.ToHashSet();

    public bool TryStart(DecisionJob job, Func<DecisionJob, CancellationToken, Task<DecisionResult>> decide)
    {
        if (capacity < 1 || FreeSlots == 0 || pending.ContainsKey(job.AgentId) || requests.IsCancellationRequested)
            return false;

        pending.Add(job.AgentId, new PendingDecision(job, decide(job, requests.Token)));
        return true;
    }

    public PendingDecision[] TakeCompleted()
    {
        var completed = pending.Values.Where(request => request.Task.IsCompleted).ToArray();
        foreach (var request in completed)
            pending.Remove(request.Job.AgentId);
        return completed;
    }

    public async Task WaitForCompletion(CancellationToken cancellationToken)
    {
        if (Count > 0)
            await Task.WhenAny(pending.Values.Select(request => request.Task)).WaitAsync(cancellationToken);
    }

    public async Task<int> CancelAndDrain()
        => (await CancelAndDrainRequests()).Length;

    public async Task<DecisionJob[]> CancelAndDrainRequests()
    {
        requests.Cancel();
        var abandoned = pending.Values.ToArray();
        try
        {
            await Task.WhenAll(abandoned.Select(request => request.Task));
        }
        catch (Exception) when (abandoned.All(request => request.Task.IsCompleted))
        {
        }
        pending.Clear();
        return abandoned.Select(request => request.Job).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await CancelAndDrain();
        requests.Dispose();
    }
}
