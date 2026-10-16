using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewPreparationWorkerTests
{
    [Fact]
    public async Task Queue_CanceledPendingPreparation_NeverInvokesBuilderAndKeepsOneActiveOperation()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> prepared = [];
        CallbackBuilder builder = new(content =>
        {
            prepared.Add(content.BodyHtml);
            if (content.BodyHtml == "active")
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        using PreviewPreparationWorker worker = new(builder);
        Task<PreparedPreviewContent> active = worker.PrepareAsync(Content("active"), null, false, true, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using CancellationTokenSource obsolete = new();
            Task<PreparedPreviewContent> canceled = worker.PrepareAsync(Content("obsolete"), null, false, true, obsolete.Token);
            Task<PreparedPreviewContent> latest = worker.PrepareAsync(Content("latest"), null, false, true, TestContext.Current.CancellationToken);
            obsolete.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.False(latest.IsCompleted);
            release.Set();
            await active;
            await latest;
            Assert.Equal(["active", "latest"], prepared);
        }
        finally
        {
            release.Set();
            await active;
        }
    }

    [Fact]
    public async Task Replacement_UsesWorkerQueue_AndRejectsAfterDispose()
    {
        using PreviewPreparationWorker worker = new();
        string result = await worker.BuildReplacementAsync(Content("<p>fallback</p>"), TestContext.Current.CancellationToken);
        Assert.Contains("fallback", result, StringComparison.Ordinal);
        worker.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.BuildReplacementAsync(Content("late"), TestContext.Current.CancellationToken));
    }

    private static PreviewContent Content(string body) => new(body, null, string.Empty, string.Empty, RemoteImagePolicy.BlockAll);

    private sealed class CallbackBuilder(Action<PreviewContent> callback) : IPreviewContentBuilder
    {
        public PreparedPreviewContent Prepare(PreviewContent content, IReadOnlyList<MarkdownRenderBlock>? previousBlocks,
            bool navigate, bool forceBodyReplacement, CancellationToken cancellationToken)
        {
            callback(content);
            return new(null, content.BodyHtml, false, false, null);
        }

        public string BuildReplacement(PreviewContent content, CancellationToken cancellationToken) => content.BodyHtml;
    }
}
