using WhoIsMarkdown.App.Infrastructure;

namespace WhoIsMarkdown.App.ViewModels;

/// <summary>
/// Public binding source for one remote-image rule row. WPF reflection binding
/// cannot reliably discover internal item types when an ItemsControl materializes
/// its template, which previously left newly added rows invisible at runtime.
/// </summary>
public sealed class RemoteImageRuleEditorItemViewModel : ObservableObject
{
    private string kindId;
    private string value;

    public RemoteImageRuleEditorItemViewModel(string kindId, string value)
    {
        this.kindId = kindId;
        this.value = value;
    }

    public string KindId
    {
        get => kindId;
        set => SetProperty(ref kindId, value);
    }

    public string Value
    {
        get => value;
        set => SetProperty(ref this.value, value);
    }
}

public sealed record RemoteImageMatchOption(string Id, string DisplayName, string Example);
