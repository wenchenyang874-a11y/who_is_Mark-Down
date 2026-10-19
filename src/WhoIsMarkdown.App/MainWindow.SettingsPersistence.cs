using System.Windows;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Bug fix: preference writes and migration used to flush disks on the dispatcher.
/// Queue immutable values, report worker failures on the UI, and drain before the
/// terminal close decision. Cancellation never means native I/O was terminated.
/// </summary>
public partial class MainWindow
{
    private ApplicationSettingsWriter? settingsWriter;
    private Task? settingsInitialization;
    private bool settingsCloseFlushApproved;
    private bool settingsSaveErrorVisible;

    private void SettingsWriter_WriteCompleted(SettingsWriteResult result)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (windowClosed || !ReferenceEquals(settingsWriter?.LastResult, result)) return;
            if (!result.Succeeded)
            {
                settingsSaveErrorVisible = true;
                UpdateStatus(result.ErrorMessage ?? "本地设置保存失败；关闭时将再次尝试。");
            }
            else if (settingsSaveErrorVisible)
            {
                settingsSaveErrorVisible = false;
                UpdateStatus("本地设置已保存");
            }
        });
    }

    private async Task<bool> FlushSettingsBeforeCloseAsync()
    {
        using DiagnosticOperation? flush = DiagnosticLog.Current?.Begin(DiagnosticStage.SettingsFlush, diagnosticWindowId);
        SettingsWriteResult result;
        string phase = "读取初始设置";
        try
        {
            if (settingsWriter is null && settingsInitialization is not null)
                await settingsInitialization.WaitAsync(TimeSpan.FromSeconds(5));
            phase = "保存本地设置";
            result = settingsWriter is null ? new(true)
                : await settingsWriter.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            flush?.Fail(new TimeoutException());
            result = new(false, $"{phase}等待已超过 5 秒；底层读写可能仍在执行，并未被强行终止。");
        }
        if (result.Succeeded) { flush?.Succeed(); return true; }
        DiagnosticLog.Current?.Record(DiagnosticStage.SettingsFlush, DiagnosticOutcome.Failed, diagnosticWindowId);
        UpdateStatus(result.ErrorMessage);
        return MessageBox.Show(this,
            $"{result.ErrorMessage}\n\n本次部分设置可能没有保存。是否仍然关闭 WIMD？\n选择“否”可返回，稍后关闭时会再次尝试保存设置。",
            "本地设置尚未保存", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void DisposeSettingsPersistence()
    {
        if (settingsWriter is null) return;
        settingsWriter.WriteCompleted -= SettingsWriter_WriteCompleted;
        settingsWriter.Stop();
        settingsWriter = null;
    }
}
