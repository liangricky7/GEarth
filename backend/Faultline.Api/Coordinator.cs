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
        var capacity = ReadNumber("FAULTLINE_CAPACITY", 64, 1, 128);
        var population = ReadNumber("FAULTLINE_POPULATION", 1100, 100, 100000);
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Environment.GetEnvironmentVariable("WORKER_TOKEN") ?? "");
        var id = $"world_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
        var world = SocietyFactory.Create(id, live, population,
            ReadNumber("FAULTLINE_SEED", 20261010, 1, int.MaxValue),
            ReadNumber("FAULTLINE_GOVERNMENT_ROTATION", 0, 0, 4));
        world.Emit("decision_accounting_started", "rule", [world.Id], new { version = 1 });
        world.Society!.WorkerCapacity = capacity;
        world.Society.TickIntervalMilliseconds = interval;
        world.Society.InstitutionIntervalSeconds = ReadNumber("FAULTLINE_INSTITUTION_SECONDS", 120, 10, 3600);
        world.Society.ResidentIntervalSeconds = ReadNumber("FAULTLINE_RESIDENT_SECONDS", 600, 10, 7200);
        await using var pending = new DecisionQueue(capacity, cancellationToken);
        long revision = 0;

        async Task Commit(CancellationToken? token = null)
        {
            world.UpdatedAt = DateTimeOffset.UtcNow;
            SocietyScheduler.UpdateMetrics(world, pending.AgentIds, world.UpdatedAt);
            world.Revision = revision + 1;
            await store.Save(world, revision, token ?? cancellationToken);
            revision = world.Revision;
            world.Events = world.Events.TakeLast(512).ToList();
            current = Wire.Copy(world);
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

        try
        {
            await Commit();
            await notes.Record(current!);
            for (var tick = 1; tick <= tickLimit; tick++)
            {
                var started = DateTimeOffset.UtcNow;
                ApplyCompleted();
                SocietyEngine.Advance(world);
                if (tick < tickLimit)
                {
                    var agents = SocietyScheduler.Select(world, pending.AgentIds, pending.FreeSlots, started);
                    foreach (var agent in agents)
                    {
                        var job = SocietyEngine.Observe(world, agent.Id, started);
                        if (pending.TryStart(job, (request, token) => Decide(request, live, token)))
                        {
                            world.Emit("decision_requested", "rule", [agent.Id], new
                            {
                                decision_id = job.RequestId, requested_at = started,
                                observation_tick = job.ObservationTick, job.Deadline, mode = world.Mode
                            });
                            agent.LastAttemptAt = started;
                            agent.LastAttemptTick = world.Tick;
                        }
                    }
                    SocietyScheduler.UpdateMetrics(world, pending.AgentIds, started);
                }
                ApplyCompleted();
                if (tick % 5 == 0)
                    await Commit();
                var delay = interval - (DateTimeOffset.UtcNow - started).TotalMilliseconds;
                if (delay > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
            while (pending.Count > 0)
            {
                await pending.WaitForCompletion(cancellationToken);
                ApplyCompleted();
            }
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
            world.Status = "stopped";
            world.Emit("run_stopped", "rule", [world.Id], new
            {
                reason = "host_shutdown",
                canceled_pending_requests = abandoned.Length,
                canceled_requests_applied = false
            });
            await Commit(CancellationToken.None);
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
