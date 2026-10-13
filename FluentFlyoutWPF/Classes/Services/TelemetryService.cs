using System.Threading.Tasks;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Telemetry is completely disabled in FluentFlyout KoksMen Edition.
/// </summary>
public static class TelemetryService
{
    public static Task SendTelemetryEventAsync(string eventName, string? experimentId = null)
    {
        // No-op: Telemetry disabled
        return Task.CompletedTask;
    }
}