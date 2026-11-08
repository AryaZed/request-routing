using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestRouting.Application;

namespace RequestRouting.Infrastructure;

/// <summary>
/// Normal operation never needs this: every commit dispatches straight away. It recovers the one gap
/// that cannot be closed from inside a request, a process dying between its commit and its dispatch.
/// Every instance runs one, with no leader election, because <see cref="Dispatcher"/> is safe to run
/// concurrently. It also covers changes made directly in the database.
/// </summary>
public sealed partial class QueueSweeper(
    Dispatcher dispatcher,
    IOptions<QueueOptions> options,
    ILogger<QueueSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            var assigned = await dispatcher.DrainAsync(ct);
            if (assigned > 0)
            {
                LogRecovered(logger, assigned);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database may be down for a while. The next tick tries again; the host must stay up.
            LogSweepFailed(logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Information, "Queue sweep assigned {Count} waiting requests.")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(LogLevel.Error, "Queue sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
