using System.Diagnostics;
using WhoIsMarkdown.Core.Diagnostics;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// One process-lifetime listener, never a Window capture. First-render phases are
/// logged; later fast phases are suppressed to avoid a typing log storm. Only
/// fixed Core names mapping to the typed stage allowlist may reach the log.
/// </summary>
internal static class CoreDiagnosticBridge
{
    internal static AsyncLocal<bool> Detailed { get; } = new();
    private static readonly ActivityListener Listener = CreateListener();

    internal static void Start() => GC.KeepAlive(Listener);

    private static ActivityListener CreateListener()
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == CoreOperationTrace.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => DiagnosticLog.Current is null
                ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (Enum.TryParse(activity.OperationName, out DiagnosticStage stage) && Enum.IsDefined(stage))
                    activity.SetCustomProperty("operation", DiagnosticLog.Current?.Begin(stage,
                        onlySlow: !Detailed.Value && !activity.OperationName.StartsWith("Image", StringComparison.Ordinal)));
            },
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("Value.", StringComparison.Ordinal)
                    && activity.GetCustomProperty("numeric_value") is long value
                    && Enum.TryParse(activity.OperationName[6..], out DiagnosticStage valueStage) && Enum.IsDefined(valueStage))
                    DiagnosticLog.Current?.Mark(valueStage, value: value);
                if (activity.GetCustomProperty("operation") is DiagnosticOperation operation)
                {
                    if (activity.GetCustomProperty("exception") is Exception exception) operation.Fail(exception);
                    else operation.Succeed();
                    operation.Dispose();
                }
                if (activity.GetCustomProperty("elapsed_ms") is double duration
                    && (Detailed.Value || duration >= 500 || activity.OperationName.StartsWith("Aggregate.Image", StringComparison.Ordinal))
                    && activity.OperationName.StartsWith("Aggregate.", StringComparison.Ordinal)
                    && Enum.TryParse(activity.OperationName[10..], out DiagnosticStage stage) && Enum.IsDefined(stage))
                    DiagnosticLog.Current?.Record(stage, DiagnosticOutcome.Point, duration: duration);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
