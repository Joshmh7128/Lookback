using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lookback.Recorder;

/// <summary>
/// With <c>Recorder:ParentProcessId</c> set (the shell passes its own pid),
/// stops the recorder when that process exits, however it exits: closed,
/// crashed or killed. Without it, a killed shell left the recorder running.
/// </summary>
internal sealed partial class ParentWatcher(IOptions<RecorderOptions> options, IHostApplicationLifetime lifetime, ILogger<ParentWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ParentProcessId is not { } pid)
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            LogParentGone(logger, pid);
            lifetime.StopApplication();
            return;
        }

        using (parent)
        {
            try
            {
                await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        LogParentGone(logger, pid);
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Parent process {Pid} is gone; stopping")]
    private static partial void LogParentGone(ILogger logger, int pid);
}
