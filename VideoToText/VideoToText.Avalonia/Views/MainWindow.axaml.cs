using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using VideoToText.Avalonia.ViewModels;

namespace VideoToText.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "변환할 영상 파일 선택",
            AllowMultiple = false,
            FileTypeFilter = new[] { 
                new FilePickerFileType("비디오 파일") { Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.mp3", "*.wav" } }
            }
        });

        if (files.Any())
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.VideoPath = files[0].Path.LocalPath;
                vm.Status = "영상 선택됨: " + System.IO.Path.GetFileName(vm.VideoPath);
            }
        }
    }

    public async void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "자막 저장 폴더 선택",
            AllowMultiple = false
        });

        if (folders.Any())
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OutputPath = folders[0].Path.LocalPath;
                vm.Status = "출력 폴더 설정됨: " + vm.OutputPath;
            }
        }
    }
}