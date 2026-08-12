using WhoIsMarkdown.App.Infrastructure;
using WhoIsMarkdown.App.Shortcuts;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.ViewModels;

internal sealed class ShortcutEditorItemViewModel : ObservableObject
{
    private ShortcutGesture gesture;
    private bool isCapturing;

    public ShortcutEditorItemViewModel(
        ShortcutCommandDefinition definition,
        ShortcutGesture currentGesture)
    {
        Definition = definition;
        gesture = currentGesture;
    }

    public ShortcutCommandDefinition Definition { get; }

    public string CommandId => Definition.Id;

    public string DisplayName => Definition.DisplayName;

    public ShortcutGesture Gesture
    {
        get => gesture;
        set
        {
            if (SetProperty(ref gesture, value))
            {
                OnPropertyChanged(nameof(GestureText));
            }
        }
    }

    public string GestureText => IsCapturing
        ? "请按新的组合键…"
        : ShortcutCatalog.FormatGesture(Gesture);

    public bool IsCapturing
    {
        get => isCapturing;
        set
        {
            if (SetProperty(ref isCapturing, value))
            {
                OnPropertyChanged(nameof(GestureText));
            }
        }
    }
}
