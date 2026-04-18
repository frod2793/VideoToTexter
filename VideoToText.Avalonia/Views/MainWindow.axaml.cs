using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using VideoToText.Avalonia.ViewModels;
using VideoToText.Core;

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
            Title = "변환할 영상 파일들 선택",
            AllowMultiple = true,
            FileTypeFilter = new[] { 
                new FilePickerFileType("미디어 파일") { Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.mp3", "*.wav", "*.m4a" } }
            }
        });

        if (files.Any())
        {
            if (DataContext is MainWindowViewModel vm)
            {
                foreach (var file in files)
                {
                    var path = file.Path.LocalPath;
                    vm.AddFileToQueue(path);
                }
                
                vm.Status = $"{files.Count}개의 파일이 대기열에 추가되었습니다.";
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