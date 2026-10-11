using Faultline.Api;

namespace Faultline.Tests;

public sealed class SimulationPauseTests
{
    [Fact]
    public async Task PauseWaitsForSavedDecisionsThenResumesWithoutDiscardingTheWorld()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "pause"), "");
            var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pause = new SimulationPause(directory);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var waiting = pause.WaitAsync(() => settle.Task, cancellation.Token);
            Assert.False(File.Exists(Path.Combine(directory, "paused")));
            settle.SetResult();
            while (!File.Exists(Path.Combine(directory, "paused")))
                await Task.Delay(10, cancellation.Token);
            Assert.False(waiting.IsCompleted);
            File.Delete(Path.Combine(directory, "pause"));
            await waiting;
            Assert.False(File.Exists(Path.Combine(directory, "paused")));
            Assert.False(pause.Requested);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ShutdownWhilePausedClearsAcknowledgementAndPropagatesCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "pause"), "");
            using var cancellation = new CancellationTokenSource();
            var pause = new SimulationPause(directory);
            var waiting = pause.WaitAsync(() => Task.CompletedTask, cancellation.Token);
            Assert.True(File.Exists(Path.Combine(directory, "paused")));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(File.Exists(Path.Combine(directory, "paused")));
            Assert.True(pause.Requested);
        }
        finally { Directory.Delete(directory, true); }
    }
}
