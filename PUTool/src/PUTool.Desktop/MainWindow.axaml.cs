using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PUTool.Desktop.ViewModels;

namespace PUTool.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.PickFolderAsync = PickFolderAsync;
        viewModel.ConfirmApplyAsync = ConfirmApplyAsync;
        viewModel.SaveMarkdownPathAsync = SaveMarkdownPathAsync;
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose solution or repository folder",
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> SaveMarkdownPathAsync(string? suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save update plan",
            SuggestedFileName = suggestedName ?? "PUTool-Update-Plan.md",
            DefaultExtension = "md",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Markdown") { Patterns = [ "*.md" ] }
            }
        });

        return file?.Path.LocalPath;
    }

    private async Task<bool> ConfirmApplyAsync(string message, bool createBackup)
    {
        var dialog = new Window
        {
            Title = "Confirm package updates",
            Width = 520,
            Height = 285,
            MinWidth = 540,
            MinHeight = 285,
            MaxWidth = 540,
            MaxHeight = 285,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#101B2D"))
        };

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 14
        };

        body.Children.Add(new TextBlock
        {
            Text = "Apply the update plan?",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F0F5FC"))
        });

        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A5B5CB"))
        });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 9
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 92
        };

        var apply = new Button
        {
            Content = createBackup ? "Create backup & apply" : "Apply without backup",
            MinWidth = 172,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3979D6")),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("White"))
        };

        cancel.Click += (_, _) => dialog.Close(false);
        apply.Click += (_, _) => dialog.Close(true);

        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        body.Children.Add(buttons);
        dialog.Content = body;

        return await dialog.ShowDialog<bool>(this);
    }
}