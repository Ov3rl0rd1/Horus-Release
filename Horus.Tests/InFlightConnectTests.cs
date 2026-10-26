using Horus.Application;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// A connect takes seconds — the API, every offer proved in turn, the adapter — and pressing
/// the button meanwhile has to stop it. Before this, a disconnect waited for the connect to
/// finish (Windows) or tore the tunnel down underneath it while it kept going (Android).
/// </summary>
public class InFlightConnectTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_disconnect_cancels_the_connect_and_returns_once_it_has_unwound()
    {
        var attempt = new InFlightConnect();
        var started = new TaskCompletionSource();
        var unwound = false;

        var connect = attempt.RunAsync(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                await Task.Delay(50, CancellationToken.None);
                unwound = true;
            }
        }, CancellationToken.None);

        await started.Task.WaitAsync(Patience);
        await attempt.CancelAsync().WaitAsync(Patience);

        Assert.True(unwound);
        Assert.False(attempt.IsRunning);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
    }

    [Fact]
    public async Task Cancelling_with_nothing_in_flight_returns_at_once()
    {
        var attempt = new InFlightConnect();

        await attempt.CancelAsync().WaitAsync(Patience);

        Assert.False(attempt.IsRunning);
    }

    [Fact]
    public async Task A_cancelled_connect_does_not_cancel_the_next_one()
    {
        var attempt = new InFlightConnect();
        var first = attempt.RunAsync(ct => Task.Delay(Timeout.Infinite, ct), CancellationToken.None);
        await attempt.CancelAsync().WaitAsync(Patience);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var ran = false;
        await attempt.RunAsync(ct =>
        {
            ran = !ct.IsCancellationRequested;
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(Patience);

        Assert.True(ran);
    }

    [Fact]
    public async Task The_callers_own_token_still_cancels_it()
    {
        var attempt = new InFlightConnect();
        using var caller = new CancellationTokenSource();

        var connect = attempt.RunAsync(ct => Task.Delay(Timeout.Infinite, ct), caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Patience));
        Assert.False(attempt.IsRunning);
    }

    [Fact]
    public async Task A_failure_is_reported_as_itself()
    {
        var attempt = new InFlightConnect();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attempt.RunAsync(_ => throw new InvalidOperationException("no offer answered"), CancellationToken.None));

        Assert.Equal("no offer answered", failure.Message);
        Assert.False(attempt.IsRunning);
    }

    [Fact]
    public async Task Cancelling_never_throws_whatever_the_connect_dies_of()
    {
        var attempt = new InFlightConnect();
        var started = new TaskCompletionSource();

        var connect = attempt.RunAsync(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new InvalidOperationException("core stopped mid-start"); }
        }, CancellationToken.None);

        await started.Task.WaitAsync(Patience);
        await attempt.CancelAsync().WaitAsync(Patience);

        await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
    }
}
