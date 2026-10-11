using System.Net.Http.Headers;
using Faultline.Core;

namespace Faultline.Api;

public sealed class Coordinator(IWorldStore store, RunNotesService notes, ILogger<Coordinator> logger) : BackgroundService
{
    private readonly HttpClient worker = new() { Timeout = TimeSpan.FromSeconds(65) };
    private volatile World? current;

    public World? Current => current;
    public string? Error { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.Initialize(cancellationToken);

            var savedRunId = Environment.GetEnvironmentVariable("FAULTLINE_SAVED_RUN_ID");
            if (!string.IsNullOrEmpty(savedRunId))
            {
                current = await store.Load(savedRunId, cancellationToken)
                    ?? throw new InvalidOperationException("Saved run missing");
                return;
            }
            var live = Environment.GetEnvironmentVariable("FAULTLINE_LIVE") == "true";
            if (Environment.GetEnvironmentVariable("FAULTLINE_WORLD") == "true")
            {
                await RunSociety(live, cancellationToken);
                return;
            }
            var tickLimit = ReadNumber("FAULTLINE_TICKS", 16, 1, 120);
            var intervalMilliseconds = ReadNumber("FAULTLINE_INTERVAL_MS", 3000, 0, 30000);
            var workerToken = Environment.GetEnvironmentVariable("WORKER_TOKEN") ?? "";

            worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);

            var scenario = await File.ReadAllTextAsync("docs/faultline/scenario.demo.json", cancellationToken);
            var runId = $"stage1_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
            var initialWorld = Engine.Create(scenario, runId, live);
            initialWorld.Revision = 1;

            await store.Save(initialWorld, 0, cancellationToken);
            current = initialWorld;

            for (var tick = 1; tick <= tickLimit; tick++)
            {
                if (intervalMilliseconds > 0)
                    await Task.Delay(intervalMilliseconds, cancellationToken);

                var nextWorld = Wire.Copy(current);
                Engine.Advance(nextWorld);

                var jobs = SelectAgents(nextWorld)
                    .Select(agentId => Engine.Observe(nextWorld, agentId, DateTimeOffset.UtcNow))
                    .ToArray();

                var results = await Task.WhenAll(jobs.Select(job => Decide(job, live, cancellationToken)));

                for (var index = 0; index < jobs.Length; index++)
                    Engine.Apply(nextWorld, jobs[index], results[index], DateTimeOffset.UtcNow);

                if (tick == tickLimit)
                {
                    nextWorld.Status = "completed";
                    nextWorld.Emit("run_completed", "rule", [nextWorld.Id], new
                    {
                        stage = 1,
                        decision_count = nextWorld.Decisions.Count
                    });
                }

                nextWorld.UpdatedAt = DateTimeOffset.UtcNow;
                nextWorld.Revision = current.Revision + 1;

                await store.Save(nextWorld, current.Revision, cancellationToken);
                current = nextWorld;

                logger.LogInformation("Committed tick {Tick}: {Count} decisions to {Store}",
                    nextWorld.Tick, nextWorld.Decisions.Count, store.Kind);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (current is { Status: "running" })
            {
                var stopped = Wire.Copy(current);
                stopped.Status = "stopped";
                stopped.Emit("run_stopped", "rule", [stopped.Id], new { reason = "host_shutdown", checkpoint_only = true });
                stopped.Revision++;
                stopped.UpdatedAt = DateTimeOffset.UtcNow;
                await store.Save(stopped, current.Revision, CancellationToken.None);
                current = stopped;
            }
        }
        catch (Exception exception)
        {
            Error = exception is InvalidOperationException
                ? "Storage or configuration failure; simulation paused"
                : $"{exception.GetType().Name}; simulation paused";

            logger.LogError("{Error}. Remote response details suppressed.", Error);
        }
        finally
        {
            if (current is not null)
            {
                try
                {
                    await notes.Record(current, Error is null ? current.Status : "paused");
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Run notes could not be finalized ({Type}); committed events remain available", exception.GetType().Name);
                }
            }
        }
    }

    private async Task RunSociety(bool live, CancellationToken cancellationToken)
    {
        var tickLimit = ReadNumber("FAULTLINE_TICKS", 360, 1, 10000);
        var interval = ReadNumber("FAULTLINE_INTERVAL_MS", 2000, 0, 30000);
        var capacity = ReadNumber("FAULTLINE_CAPACITY", 64, 1, 384);
        var population = ReadNumber("FAULTLINE_POPULATION", 1100, 160, 100000);
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Environment.GetEnvironmentVariable("WORKER_TOKEN") ?? "");
        var resumeId = Environment.GetEnvironmentVariable("FAULTLINE_RESUME_RUN_ID");
        World world;
        if (string.IsNullOrEmpty(resumeId))
        {
            var id = $"world_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
            world = SocietyFactory.Create(id, live, population,
                ReadNumber("FAULTLINE_SEED", 20261010, 1, int.MaxValue),
                ReadNumber("FAULTLINE_GOVERNMENT_ROTATION", 0, 0, 4));
            world.Emit("decision_accounting_started", "rule", [world.Id], new { version = 1 });
        }
        else
        {
            // A forked timeline: continue a saved run (on its own Neon branch) from a week-boundary checkpoint.
            world = await store.Load(resumeId, cancellationToken) ?? throw new InvalidOperationException("Resume run missing");
            if (world.Society is null || world.Status != "running"
                || (world.Tick != 0 && (world.Tick + 1) % SocietyLabor.DaysPerWeek != 0))
                throw new InvalidOperationException("Resume needs a running week-boundary checkpoint");
            world.Mode = live ? "LIVE" : "RULE";
            var change = Environment.GetEnvironmentVariable("FAULTLINE_FORK_GOVERNMENT")?.Split('=');
            world.Emit("timeline_forked", "rule", [world.Id], new
            {
                from_tick = world.Tick,
                timeline = Environment.GetEnvironmentVariable("FAULTLINE_TIMELINE"),
                change = change is [var country, var government] ? new { country, government } : null
            });
            if (change is [var countryId, var profileId])
                SocietyFactory.ChangeGovernment(world, countryId, profileId);
        }
        world.Society!.WorkerCapacity = capacity;
        world.Society.TickIntervalMilliseconds = interval;
        var providerConfig = Environment.GetEnvironmentVariable("FAULTLINE_COMPUTE_PROVIDERS");
        if (!string.IsNullOrEmpty(providerConfig))
            world.Society.ComputeProviders = System.Text.Json.JsonSerializer.Deserialize<ComputeProvider[]>(providerConfig, Wire.Json) ?? [];
        if (live && world.Society.ComputeProviders.Length > 0)
            foreach (var group in new[] { "institution", "citizen" })
                if (!world.Society.ComputeProviders.Any(provider => provider.Capacity > 0 && (provider.DecisionGroup == group || provider.DecisionGroup == "all")))
                    throw new InvalidOperationException($"No model provider serves {group} decisions");
        await using var pending = new DecisionQueue(capacity, cancellationToken);
        var queue = new Queue<string>();
        long revision = world.Revision;
        // Verbose runs also write the day-by-day history file the renderer replays (a fork's file is named after its timeline).
        using var history = Environment.GetEnvironmentVariable("FAULTLINE_VERBOSE") == "true"
            ? new HistoryWriter(HistoryWriter.PathFor(Environment.GetEnvironmentVariable("FAULTLINE_TIMELINE") is { Length: > 0 } timeline
                ? timeline : world.Id), world) : null;
        history?.Day(world);

        async Task Commit(CancellationToken? token = null)
        {
            world.UpdatedAt = DateTimeOffset.UtcNow;
            SocietyScheduler.UpdateMetrics(world, pending.AgentIds, queue, world.UpdatedAt);
            world.Revision = revision + 1;
            // One serialization feeds both the store and the API's read-only copy.
            var serialized = System.Text.Json.JsonSerializer.Serialize(world, Wire.Json);
            await store.Save(world, revision, token ?? cancellationToken, serialized);
            revision = world.Revision;
            history?.Flush();
            world.Events = world.Events.TakeLast(512).ToList();
            var copy = System.Text.Json.JsonSerializer.Deserialize<World>(serialized, Wire.Json)!;
            copy.Events = copy.Events.TakeLast(512).ToList();
            current = copy;
            logger.LogInformation("World tick {Tick}: {Coverage}/{Population} model coverage, {Pending} pending",
                world.Tick, world.Agents.Values.Count(agent => agent.ModelDecisions > 0), world.Agents.Count, pending.Count);
        }

        void ApplyCompleted()
        {
            foreach (var request in pending.TakeCompleted())
            {
                SocietyEngine.Apply(world, request.Job, request.Task.GetAwaiter().GetResult(), DateTimeOffset.UtcNow);
            }
        }

        void Dispatch()
        {
            var pendingIds = pending.AgentIds;
            queue = new Queue<string>(queue.Where(id => world.Agents.TryGetValue(id, out var agent)
                && SocietyLabor.DecidesForItself(agent) && !pendingIds.Contains(id)));
            var started = new HashSet<string>();
            foreach (var agent in SocietyScheduler.SelectQueued(world, queue, pending.AgentIds, pending.FreeSlots))
            {
                var now = DateTimeOffset.UtcNow;
                var job = SocietyEngine.Observe(world, agent.Id, now);
                if (pending.TryStart(job, (request, token) => Decide(request, live, token)))
                {
                    world.Emit("decision_requested", "rule", [agent.Id], new
                    {
                        decision_id = job.RequestId, requested_at = now,
                        observation_tick = job.ObservationTick, job.Deadline, mode = world.Mode
                    });
                    agent.LastAttemptAt = now;
                    agent.LastAttemptTick = world.Tick;
                    started.Add(agent.Id);
                }
            }
            queue = new Queue<string>(queue.Where(id => !started.Contains(id)));
            SocietyScheduler.UpdateMetrics(world, pending.AgentIds, queue, DateTimeOffset.UtcNow);
        }

        async Task DrainWeek()
        {
            while (queue.Count > 0 || pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyCompleted();
                Dispatch();
                if (pending.Count > 0)
                    await pending.WaitForCompletion(cancellationToken);
                ApplyCompleted();
            }
        }

        try
        {
            await Commit();
            await notes.Record(current!);
            var firstTick = world.Tick + 1;
            for (var tick = firstTick; tick <= tickLimit; tick++)
            {
                var started = DateTimeOffset.UtcNow;
                ApplyCompleted();
                if ((world.Tick + 1) % SocietyLabor.DaysPerWeek == 0)
                {
                    await DrainWeek();
                    // Week-boundary checkpoint: nothing in flight or queued, so a fork resumes from it exactly.
                    if (tick > firstTick)
                        await Commit();
                }
                SocietyEngine.Advance(world);
                if ((world.Tick == 1 || world.Tick % SocietyLabor.DaysPerWeek == 0) && tick < tickLimit)
                    foreach (var agent in SocietyScheduler.WeeklyCohort(world))
                        queue.Enqueue(agent.Id);
                if (tick < tickLimit)
                    Dispatch();
                ApplyCompleted();
                history?.Day(world);
                var nextTick = started.AddMilliseconds(interval);
                while (DateTimeOffset.UtcNow < nextTick)
                {
                    var delay = nextTick - DateTimeOffset.UtcNow;
                    if (delay <= TimeSpan.Zero)
                        break;
                    if (!live || tick == tickLimit || pending.Count == 0)
                    {
                        await Task.Delay(delay, cancellationToken);
                        break;
                    }
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(delay);
                    try
                    {
                        await pending.WaitForCompletion(wait.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyCompleted();
                    Dispatch();
                }
            }
            await DrainWeek();
            world.Status = "completed";
            world.Emit("run_completed", "rule", [world.Id], new
            {
                stage = 2,
                world.Society.ModelDecisionCount,
                model_coverage = world.Agents.Values.Count(agent => agent.ModelDecisions > 0)
            });
            SocietyEconomy.AssertConservation(world);
            await Commit();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var abandoned = await pending.CancelAndDrainRequests();
            foreach (var job in abandoned)
                world.Emit("decision_canceled", "rule", [job.AgentId], new
                {
                    decision_id = job.RequestId, observation_tick = job.ObservationTick,
                    canceled_at = DateTimeOffset.UtcNow, reason = "host_shutdown"
                });
            var queued = queue.Count;
            queue.Clear();
            world.Status = "stopped";
            world.Emit("run_stopped", "rule", [world.Id], new
            {
                reason = "host_shutdown",
                canceled_pending_requests = abandoned.Length,
                canceled_queued_requests = queued,
                canceled_requests_applied = false
            });
            await Commit(CancellationToken.None);
        }
        finally
        {
            history?.End(world);
        }
    }

    private static IEnumerable<string> SelectAgents(World world)
    {
        var agents = world.Agents.Values
            .OrderBy(agent => agent.Role)
            .ThenBy(agent => agent.Id.Split('_').Last())
            .ThenBy(agent => agent.CountryId)
            .ToArray();

        var selected = new List<string>();

        if (world.Tick is 1 or 9)
            selected.Add("beryl_business_1");

        while (selected.Count < 4)
        {
            var agentId = agents[world.SchedulerCursor++ % agents.Length].Id;

            if (!selected.Contains(agentId))
                selected.Add(agentId);
        }

        return selected;
    }

    private Task<DecisionResult> Decide(DecisionJob job, bool live, CancellationToken cancellationToken)
        => WorkerClient.Decide(worker, job, live, cancellationToken);

    private static int ReadNumber(string name, int defaultValue, int minimum, int maximum)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : defaultValue;
    }
}
