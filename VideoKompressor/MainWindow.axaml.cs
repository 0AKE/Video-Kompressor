using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VideoKompressor.ViewModels;

namespace VideoKompressor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a video file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.wmv"],
                },
            ],
        });

        if (files.Count > 0 && Vm is not null)
            Vm.InputFilePath = files[0].TryGetLocalPath();
    }

    private async void OnSetOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an output folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && Vm is not null)
            Vm.OutputFolder = folders[0].TryGetLocalPath();
    }
}
