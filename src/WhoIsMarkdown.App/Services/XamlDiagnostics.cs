using System.Windows;

namespace WhoIsMarkdown.App.Services;

// Bug fix: InitializeComponent previously appeared as one opaque four-second
// interval. Fixed checkpoints describe BAML construction order, not Loaded time.
// Never read element text, binding values, paths or document content here.
public static class XamlDiagnostics
{
    public static readonly DependencyProperty CheckpointProperty = DependencyProperty.RegisterAttached(
        "Checkpoint", typeof(XamlConstructionCheckpoint), typeof(XamlDiagnostics),
        new PropertyMetadata(XamlConstructionCheckpoint.None, OnCheckpoint));

    public static void SetCheckpoint(DependencyObject element, XamlConstructionCheckpoint value)
        => element.SetValue(CheckpointProperty, value);

    public static XamlConstructionCheckpoint GetCheckpoint(DependencyObject element)
        => (XamlConstructionCheckpoint)element.GetValue(CheckpointProperty);

    private static void OnCheckpoint(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (DiagnosticLog.Current is not { } log) return;
        XamlConstructionCheckpoint checkpoint = (XamlConstructionCheckpoint)args.NewValue;
        log.SetXamlCheckpoint(checkpoint);
        log.Mark(DiagnosticStage.XamlCheckpoint, value: (long)checkpoint);
    }
}

public enum XamlConstructionCheckpoint
{
    None, RootProperties, TitleBar, Menu, Sidebar, Toolbar, EditorContainer, EditorConstructed,
    PreviewContainer, PreviewConstructed, StatusBar,
}
