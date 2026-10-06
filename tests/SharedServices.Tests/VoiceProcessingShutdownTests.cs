using VoiceOrchestratorAgent;

namespace SharedServices.Tests;

public class VoiceProcessingShutdownTests
{
    [Fact]
    public async Task ShutdownCancelsAndAwaitsBothProducersBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        var first = WaitForCancellationAsync(cancellation.Token);
        var second = WaitForCancellationAsync(cancellation.Token);

        await VoiceProcessingShutdown.CancelAndWaitAsync(cancellation, [first, second], TimeSpan.FromSeconds(5));

        Assert.True(first.IsCompleted && second.IsCompleted);
    }

    [Fact]
    public async Task ShutdownDoesNotReturnWhileTheSecondProducerIsDraining()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = DrainAfterCancellationAsync(cancellation.Token, gate.Task);
        var shutdown = VoiceProcessingShutdown.CancelAndWaitAsync(cancellation,
            [Task.CompletedTask, second], TimeSpan.FromSeconds(5));
        gate.SetResult();

        await shutdown;

        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ProducerFailureIsNotHiddenByCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var second = WaitForCancellationAsync(cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => VoiceProcessingShutdown.CancelAndWaitAsync(
            cancellation, [Task.FromException(new InvalidOperationException("model failure")), second],
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ShutdownBudgetRejectsAProducerThatDoesNotStop()
    {
        using var cancellation = new CancellationTokenSource();
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VoiceProcessingShutdown.CancelAndWaitAsync(
                cancellation, [Task.CompletedTask, stuck.Task], TimeSpan.FromMilliseconds(20)));
        }
        finally
        {
            stuck.SetResult();
        }
    }

    [Fact]
    public async Task FinalPersistenceCanUseItsOwnBudgetAfterRequestCancellation()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        var copy = await fixture.Store.LoadAsync(address);
        using var request = new CancellationTokenSource();
        request.Cancel();
        using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await fixture.Store.SaveAsync(copy, [fixture.Text("complete")], save.Token);

        Assert.Equal("complete", Assert.Single((await fixture.Store.LoadAsync(address)).Messages).Text);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private static async Task DrainAfterCancellationAsync(CancellationToken cancellationToken, Task gate)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await gate;
        }
    }
}
