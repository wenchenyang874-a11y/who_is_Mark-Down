using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Separates the global external-edit default from an explicit current-file choice.
/// All opening entry points resolve against the active path at notification time.
/// Settings apply to future notifications, without forcing a reload when toggled.
/// </summary>
public partial class MainWindow
{
    private void ExternalChangeMenu_SubmenuOpened(object sender, RoutedEventArgs eventArgs) =>
        RefreshExternalChangeMenu();

    private void ExternalChangeDefault_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetExternalChangeBehavior(sender, out ExternalFileChangeBehavior behavior)) return;
        applicationSettings = applicationSettings with
        {
            ExternalFileChanges = applicationSettings.ExternalFileChanges with { DefaultBehavior = behavior },
        };
        ExternalChangeSettingApplied();
    }

    private void ExternalChangeFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (document.FilePath is not { } path) return;
        ExternalFileChangeBehavior? behavior = null;
        if (sender is not MenuItem { Tag: "Default" })
        {
            if (!TryGetExternalChangeBehavior(sender, out ExternalFileChangeBehavior selected)) return;
            behavior = selected;
        }
        applicationSettings = applicationSettings with
        {
            ExternalFileChanges = applicationSettings.ExternalFileChanges.WithOverride(path, behavior),
        };
        ExternalChangeSettingApplied();
    }

    private void ExternalChangeSettingApplied()
    {
        RefreshExternalChangeMenu();
        if (TrySaveApplicationSettings())
            UpdateStatus("已应用外部修改处理方式，正在后台保存；有未保存修改时仍会询问");
    }

    private void RefreshExternalChangeMenu()
    {
        ExternalFileChangeSettings settings = applicationSettings.ExternalFileChanges;
        foreach (MenuItem item in ExternalChangeDefaultMenuItem.Items.OfType<MenuItem>())
            item.IsChecked = TryGetExternalChangeBehavior(item, out ExternalFileChangeBehavior behavior)
                && behavior == settings.DefaultBehavior;

        ExternalChangeFileMenuItem.IsEnabled = document.FilePath is not null;
        ExternalChangeFileMenuItem.ToolTip = document.FilePath
            ?? "请先打开或保存文件，再为该文件设置单独的行为";
        ExternalFileChangeBehavior? fileBehavior = settings.GetOverride(document.FilePath);
        foreach (MenuItem item in ExternalChangeFileMenuItem.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is "Default" ? fileBehavior is null
                : TryGetExternalChangeBehavior(item, out ExternalFileChangeBehavior behavior) && behavior == fileBehavior;
    }

    private static bool TryGetExternalChangeBehavior(object sender, out ExternalFileChangeBehavior behavior)
    {
        behavior = default;
        return sender is MenuItem { Tag: string value }
            && Enum.TryParse(value, out behavior) && Enum.IsDefined(behavior);
    }
}
