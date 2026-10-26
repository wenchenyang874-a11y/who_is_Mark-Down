namespace WhoIsMarkdown.Core.Diagnostics;

/// <summary>
/// Optional local diagnostic hook installed by an Activity listener. Implementations
/// must retain neither Task results nor UI objects. Core owns no logging or UI backend.
/// </summary>
public interface ICoreAsyncTraceObserver
{
    public void TaskReturned(Task task, double dispatchMilliseconds);
    public void CallerResumed(Task task);
}
