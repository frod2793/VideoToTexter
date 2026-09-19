using System;
using System.Threading.Tasks;
using VideoToText.Avalonia.ViewModels;

namespace VideoToText.Checks;

internal static class MicrophoneUiChecks
{
    internal static Task CheckRecordedFileQueueValidation()
    {
        Check(MainWindowViewModel.IsSupportedMediaPath("recording.m4a"), "확정 M4A가 대기열 입력에서 거부됨");
        Check(!MainWindowViewModel.IsSupportedMediaPath("recording.m4a.part"), "미확정 part 파일이 대기열에 허용됨");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
