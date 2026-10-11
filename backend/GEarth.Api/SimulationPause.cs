namespace GEarth.Api;

// The cloud controller requests a pause; the engine acknowledges only after
// in-flight decisions have been applied and their checkpoint has been saved.
public sealed class SimulationPause(string? directory)
{
    public bool Requested => !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, "pause"));

    public async Task WaitAsync(Func<Task> settle, CancellationToken cancellationToken)
    {
        if (!Requested)
            return;
        await settle();
        if (!Requested)
            return;
        var acknowledgement = Path.Combine(directory!, "paused");
        File.WriteAllText(acknowledgement, "paused\n");
        try
        {
            while (Requested)
                await Task.Delay(100, cancellationToken);
        }
        finally
        {
            File.Delete(acknowledgement);
        }
    }
}
