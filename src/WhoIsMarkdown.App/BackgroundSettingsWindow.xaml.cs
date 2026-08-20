using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace WhoIsMarkdown.App;

/// <summary>
/// Keeps background controls alive while the native image picker owns focus.
/// A WPF Popup closes when that picker takes mouse capture, which previously
/// made the panel flash and disappear after every selection attempt.
/// </summary>
public partial class BackgroundSettingsWindow : Window
{
    private readonly bool initializing;

    public BackgroundSettingsWindow(string? backgroundPath, double visibilityPercentage)
    {
        InitializeComponent();

        initializing = true;
        try
        {
            BackgroundVisibilitySlider.Value = Math.Clamp(visibilityPercentage, 0, 100);
            UpdateVisibilityText();
            SetBackgroundPath(backgroundPath);
        }
        finally
        {
            initializing = false;
        }
    }

    public event Action<string>? BackgroundImageSelected;

    public event Action? BackgroundRemoved;

    public event Action<double>? BackgroundVisibilityChanged;

    public void SetBackgroundPath(string? path)
    {
        bool hasBackground = !string.IsNullOrWhiteSpace(path);
        BackgroundFileNameText.Text = hasBackground
            ? Path.GetFileName(path)
            : "尚未选择图片";
        BackgroundFileNameText.ToolTip = hasBackground ? path : null;
        BackgroundVisibilitySlider.IsEnabled = hasBackground;
        RemoveBackgroundButton.IsEnabled = hasBackground;
    }

    private void SelectBackground_Click(object sender, RoutedEventArgs eventArgs)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择应用背景图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };

        // This real window remains alive behind the native dialog. Do not close
        // and recreate it: that sequence caused the visible flash regression.
        if (dialog.ShowDialog(this) == true)
        {
            BackgroundImageSelected?.Invoke(dialog.FileName);
        }
    }

    private void RemoveBackground_Click(object sender, RoutedEventArgs eventArgs)
    {
        BackgroundRemoved?.Invoke();
    }

    private void BackgroundVisibilitySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        UpdateVisibilityText();
        if (!initializing)
        {
            BackgroundVisibilityChanged?.Invoke(eventArgs.NewValue);
        }
    }

    private void UpdateVisibilityText()
    {
        BackgroundVisibilityText.Text =
            $"背景可见度 {BackgroundVisibilitySlider.Value:0}%";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Close();
        }
    }
}
