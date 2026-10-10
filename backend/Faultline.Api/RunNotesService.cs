using System.Text.Json;
using Faultline.Core;

namespace Faultline.Api;

public sealed class RunNotesService(IWorldStore store, ILogger<RunNotesService> logger)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string directory = Path.GetFullPath(".data/run-notes");

    public async Task<RunNotes> Record(World world, string? status = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, world.Id + ".json");
            if (status == "paused")
            {
                var partial = RunAnalysis.Create(world, world.Events, status);
                await Export(partial, cancellationToken);
                return partial;
            }
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<RunNotes>(await File.ReadAllTextAsync(path, cancellationToken), Wire.Json);
                if (existing is not null && existing.ThroughSequence > world.Seq || existing?.ThroughSequence == world.Seq && existing.HistoryComplete && (status is null || existing.Status == status))
                    return existing;
            }
            RunNotes? stored = null;
            var storageAvailable = true;
            try
            {
                stored = await store.LoadNotes(world.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                storageAvailable = false;
                logger.LogWarning("Using checkpoint history for run notes ({Type})", exception.GetType().Name);
            }
            if (stored?.ThroughSequence == world.Seq && (status is null || stored.Status == status))
            {
                await Export(stored, cancellationToken);
                return stored;
            }
            var history = new List<WorldEvent>();
            long sequence = 0;
            try
            {
                while (storageAvailable && sequence < world.Seq)
                {
                    var page = await store.ReadEvents(world.Id, sequence, 500, cancellationToken);
                    if (page.Length == 0)
                        break;
                    history.AddRange(page.Where(item => item.Seq <= world.Seq));
                    sequence = page[^1].Seq;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                storageAvailable = false;
                logger.LogWarning("Event archive unavailable for run notes ({Type})", exception.GetType().Name);
            }
            if (!storageAvailable)
                history.AddRange(world.Events);
            var notes = RunAnalysis.Create(world, history, status);
            await Export(notes, cancellationToken);
            try
            {
                if (storageAvailable)
                    await store.SaveNotes(notes, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning("Run notes exported locally; database notes write failed ({Type})", exception.GetType().Name);
            }
            return notes;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Export(RunNotes notes, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, notes.RunId);
        await File.WriteAllTextAsync(path + ".json.tmp", JsonSerializer.Serialize(notes, Wire.Json), cancellationToken);
        File.Move(path + ".json.tmp", path + ".json", true);
        await File.WriteAllTextAsync(path + ".md.tmp", notes.Markdown, cancellationToken);
        File.Move(path + ".md.tmp", path + ".md", true);
    }
}
