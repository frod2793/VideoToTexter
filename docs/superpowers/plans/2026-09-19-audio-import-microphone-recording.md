# Audio Import and Microphone Recording Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MP3·M4A·WAV 녹음 파일과 macOS 기본/선택 마이크 녹음을 기존 VideoTexter 대기열에서 안전하게 텍스트로 변환한다.

**Architecture:** 기존 `VideoToTextApp`의 FFmpeg → WAV → Whisper → SRT/TXT 파이프라인을 그대로 사용한다. 새 `FFmpegMicrophoneRecorder`만 FFmpeg `avfoundation` 프로세스의 장치 조회·시작·정상 종료·M4A 승격을 담당하고, 정상 확정된 파일을 `MainWindowViewModel.AddFileToQueue`로 넘긴다.

**Tech Stack:** .NET 8, Avalonia 11.2.3, CommunityToolkit.Mvvm 8.2.1, Xabe.FFmpeg 5.2.6, bundled FFmpeg avfoundation/AAC, existing `VideoToText.Checks` console runner

## Global Constraints

- `.agents/AGENTS.md`는 현재 존재하지 않으며 전역 하네스와 기존 미커밋 변경을 보존한다.
- macOS 11.0 이상, Apple Silicon arm64 패키징을 유지한다.
- 새 NuGet·네이티브 오디오 의존성을 추가하지 않는다.
- MP3·M4A·WAV와 기존 영상 형식은 동일한 대기열·변환 파이프라인을 사용한다.
- 마이크 녹음은 선택된 출력 경로에 M4A로 보존한 뒤 대기열에 추가하며 자동 변환 시작은 하지 않는다.
- 기본 마이크와 사용자가 선택한 입력 장치를 모두 지원한다.
- 실시간 자막, 파형, 녹음 편집, 노이즈 제거, 다중 마이크 동시 녹음은 구현하지 않는다.
- 기존 파일을 덮어쓰지 않고 실패한 녹음을 성공 작업으로 등록하지 않는다.
- 실제 마이크·권한·한국어 품질은 자동검사와 별도 상태로 보고한다.
- 커밋·푸시는 별도 승인 전 수행하지 않는다.

---

## File Map

- Create: `VideoToText.Avalonia/Services/FFmpegMicrophoneRecorder.cs` — 장치 파싱, FFmpeg 인자, 녹음 프로세스, 임시 M4A 승격
- Modify: `VideoToText.Avalonia/ViewModels/MainWindowViewModel.cs` — 지원 확장자, 장치/녹음 상태, 명령, 대기열 연결
- Modify: `VideoToText.Avalonia/Views/MainWindow.axaml` — 영상·녹음 가져오기와 마이크 컨트롤
- Modify: `VideoToText.Avalonia/Views/MainWindow.axaml.cs` — 파일 필터 재사용, 녹음 중 창 닫기
- Modify: `VideoToText.Checks/Program.cs` — 새 검사 등록과 FFmpeg 실행 공용부
- Create: `VideoToText.Checks/AudioImportChecks.cs` — 지원 확장자와 실제 MP3/M4A 추출 검사
- Create: `VideoToText.Checks/MicrophoneRecorderChecks.cs` — 장치 파서·인자·이름 검사
- Create: `VideoToText.Checks/MicrophonePermissionChecks.cs` — 패키징 권한 설명 검사
- Modify: `build_macos.sh` — 생성 Info.plist의 마이크 사용 설명
- Modify: `Doc/FEATURE_SPEC.md`, `Doc/ARCHITECTURE.md`, `Doc/PROJECT_STATUS.md` — 구현 결과와 증명 상태

### Task 1: 기존 녹음 파일 가져오기 계약

**Files:**
- Modify: `VideoToText.Avalonia/ViewModels/MainWindowViewModel.cs`
- Modify: `VideoToText.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `VideoToText.Avalonia/Views/MainWindow.axaml`
- Modify: `VideoToText.Checks/Program.cs`

**Interfaces:**
- Produces: `MainWindowViewModel.SupportedMediaPatterns : IReadOnlyList<string>`
- Produces: `MainWindowViewModel.IsSupportedMediaPath(string path) : bool`
- Preserves: `MainWindowViewModel.AddFileToQueue(string path) : void`

- [ ] **Step 1: 지원 확장자 검사를 RED로 추가**

`VideoToText.Checks/AudioImportChecks.cs`에 검사 메서드를 먼저 추가하고 `Program.cs`에는 실행 등록만 추가한다.

```csharp
await ExecuteCheck("18. MP3·M4A·WAV와 기존 영상 확장자만 대기열 입력으로 허용", CheckSupportedMediaExtensions, () => passCount++, () => failCount++);

private static Task CheckSupportedMediaExtensions()
{
    string[] accepted = { "voice.mp3", "VOICE.M4A", "memo.wav", "video.mp4", "video.mkv", "video.mov", "video.avi" };
    foreach (string path in accepted)
    {
        Assert(MainWindowViewModel.IsSupportedMediaPath(path), $"지원 파일 거부: {path}");
    }

    Assert(!MainWindowViewModel.IsSupportedMediaPath("notes.txt"), "TXT가 미디어로 허용됨");
    Assert(!MainWindowViewModel.IsSupportedMediaPath("no-extension"), "확장자 없는 파일이 허용됨");
    return Task.CompletedTask;
}
```

- [ ] **Step 2: RED 확인**

Run:

```bash
dotnet build VideoToText.Checks/VideoToText.Checks.csproj --no-restore
```

Expected: `MainWindowViewModel.IsSupportedMediaPath`가 없어 컴파일 실패.

- [ ] **Step 3: 지원 형식의 단일 목록과 큐 진입 검증 구현**

`MainWindowViewModel`에 다음 계약을 추가한다.

```csharp
public static IReadOnlyList<string> SupportedMediaPatterns { get; } =
    new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.mp3", "*.m4a", "*.wav" };

internal static bool IsSupportedMediaPath(string path)
{
    string extension = Path.GetExtension(path);
    return SupportedMediaPatterns.Any(pattern =>
        string.Equals(extension, pattern.Substring(1), StringComparison.OrdinalIgnoreCase));
}
```

`AddFileToQueue`의 첫 분기에 다음 동작을 넣는다.

```csharp
if (!IsSupportedMediaPath(path))
{
    Status = $"지원하지 않는 미디어 형식입니다: {Path.GetFileName(path)}";
    return;
}
```

`MainWindow.axaml.cs`는 하드코딩 패턴 대신 `MainWindowViewModel.SupportedMediaPatterns`를 쓰고 다이얼로그 제목을 `변환할 영상·녹음 파일 선택`으로 바꾼다. `MainWindow.axaml`의 `영상 파일 추가...`, `Batch Video Queue`, `기본: 영상 위치` 문구를 각각 `영상·녹음 파일 추가...`, `미디어 작업 대기열`, `기본: 원본 파일 위치`로 바꾼다.

- [ ] **Step 4: GREEN 확인**

Run:

```bash
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj -- --require-ffmpeg
```

Expected: 기존 17건과 새 18번 검사 PASS.

- [ ] **Step 5: 실제 MP3/M4A 추출 검사를 RED로 추가**

FFmpeg 의존 검사 19를 등록한다.

```csharp
await ExecuteCheckWithFFmpeg("19. 실제 MP3·M4A를 16kHz mono WAV로 추출", CheckMp3AndM4aExtraction, () => passCount++, () => failCount++, () => skipCount++);
```

검사 메서드는 임시 폴더에서 다음 명령과 실제 소비자를 사용한다.

```csharp
private static async Task CheckMp3AndM4aExtraction()
{
    string root = Path.Combine(Path.GetTempPath(), "VideoToTexterChecks", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string ffmpeg = s_ffmpegExecutable!;
        foreach (string extension in new[] { ".mp3", ".m4a" })
        {
            string input = Path.Combine(root, "voice" + extension);
            string output = Path.Combine(root, "voice" + extension + ".wav");
            await RunProcessAsync(ffmpeg,
                "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                "-i", "sine=frequency=440:duration=1", "-y", input);
            bool extracted = await new FFmpegAudioExtractor().ExtractAudioAsync(input, output);
            Assert(extracted && new FileInfo(output).Length > 44, $"{extension} WAV 추출 실패");
        }
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
```

`SetupFFmpeg`가 확인한 실행 파일을 `s_ffmpegExecutable`에 보관한다. `RunProcessAsync(string executable, params string[] arguments)`는 각 항목을 `ProcessStartInfo.ArgumentList`에 그대로 넣고, 종료 코드 0이 아니면 stderr를 포함한 예외를 던진다.

- [ ] **Step 6: MP3/M4A 실제 경로 GREEN 확인**

Run:

```bash
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj -- --require-ffmpeg
```

Expected: 19/19 PASS, 0 FAIL, 0 SKIP.

- [ ] **Step 7: 변경 범위 검수**

Run:

```bash
git diff --check -- VideoToText.Avalonia/ViewModels/MainWindowViewModel.cs VideoToText.Avalonia/Views/MainWindow.axaml.cs VideoToText.Avalonia/Views/MainWindow.axaml VideoToText.Checks/Program.cs
```

Expected: 출력 없음, 종료 코드 0. 커밋은 만들지 않는다.

### Task 2: FFmpeg 마이크 녹음 서비스

**Files:**
- Create: `VideoToText.Avalonia/Services/FFmpegMicrophoneRecorder.cs`
- Create: `VideoToText.Checks/MicrophoneRecorderChecks.cs`
- Modify: `VideoToText.Checks/Program.cs` (검사 등록만)

**Interfaces:**
- Produces: `AudioInputDevice(int? Index, string Name)`
- Produces: `FFmpegMicrophoneRecorder(string ffmpegPath)`
- Produces: `ListAudioInputDevicesAsync(CancellationToken) : Task<IReadOnlyList<AudioInputDevice>>`
- Produces: `StartAsync(AudioInputDevice device, string outputDirectory, CancellationToken) : Task`
- Produces: `StopAsync(CancellationToken) : Task<string>`
- Produces: `IsRecording : bool`
- Produces internal deterministic helpers: `ParseAudioDevices`, `BuildRecordingArguments`, `GetUniqueRecordingPath`

- [ ] **Step 1: 장치 파서·인자·파일명 RED 검사 추가**

검사 20을 등록하고 다음 핵심 입력을 사용한다.

```csharp
private static Task CheckMicrophoneRecorderContracts()
{
    const string stderr = "[AVFoundation indev @ 0x1] AVFoundation video devices:\n" +
                          "[AVFoundation indev @ 0x1] [0] FaceTime HD Camera\n" +
                          "[AVFoundation indev @ 0x1] AVFoundation audio devices:\n" +
                          "[AVFoundation indev @ 0x1] [0] MacBook Microphone\n" +
                          "[AVFoundation indev @ 0x1] [1] USB Audio";

    var devices = FFmpegMicrophoneRecorder.ParseAudioDevices(stderr);
    Assert(devices.Count == 3 && devices[0].Index == null, "기본 마이크 항목 누락");
    Assert(devices[1].Index == 0 && devices[1].Name == "MacBook Microphone", "장치 0 파싱 실패");
    Assert(devices[2].Index == 1 && devices[2].Name == "USB Audio", "장치 1 파싱 실패");

    string defaultArgs = string.Join(' ', FFmpegMicrophoneRecorder.BuildRecordingArguments(devices[0], "/tmp/a.part"));
    string selectedArgs = string.Join(' ', FFmpegMicrophoneRecorder.BuildRecordingArguments(devices[2], "/tmp/b.part"));
    Assert(defaultArgs.Contains("-i :default") && defaultArgs.Contains("-f ipod"), "기본 장치 인자 오류");
    Assert(selectedArgs.Contains("-audio_device_index 1") && selectedArgs.Contains("-i :none"), "선택 장치 인자 오류");
    return Task.CompletedTask;
}
```

Run:

```bash
dotnet build VideoToText.Checks/VideoToText.Checks.csproj --no-restore
```

Expected: recorder 타입이 없어 컴파일 실패.

- [ ] **Step 2: 최소 데이터 타입과 결정적 헬퍼 구현**

`VideoToText.Avalonia/Services/FFmpegMicrophoneRecorder.cs`에 다음 타입을 만든다.

```csharp
namespace VideoToText.Avalonia.Services;

public sealed class AudioInputDevice
{
    public int? Index { get; }
    public string Name { get; }
    public AudioInputDevice(int? index, string name) { Index = index; Name = name; }
    public override string ToString() => Name;
}

public sealed class FFmpegMicrophoneRecorder : IAsyncDisposable
{
    internal static IReadOnlyList<AudioInputDevice> ParseAudioDevices(string stderr);
    internal static IReadOnlyList<string> BuildRecordingArguments(AudioInputDevice device, string tempPath);
    internal static string GetUniqueRecordingPath(string directory, DateTime now);
}
```

`ParseAudioDevices`는 stderr를 줄 단위로 읽어 `AVFoundation audio devices:`가 나온 뒤에만 정규식 `\[(\d+)\]\s+(.+)$`을 적용하고, 다른 섹션 헤더가 나오면 종료한다. 반환 목록 첫 항목은 `new AudioInputDevice(null, "시스템 기본 마이크")`이다. `BuildRecordingArguments`는 아래 토큰을 문자열 목록으로 반환하며 프로세스 실행부는 각 토큰을 `ArgumentList`에 추가한다. `GetUniqueRecordingPath`는 `녹음_yyyyMMdd_HHmmss.m4a`가 있으면 `_2`, `_3` 순서로 첫 빈 경로를 반환한다.

인자는 다음 순서를 보장한다.

```text
-hide_banner -loglevel warning -f avfoundation [-audio_device_index N] -i :default|:none -vn -c:a aac -b:a 128k -f ipod TEMP_PATH
```

- [ ] **Step 3: 헬퍼 GREEN 확인**

Run:

```bash
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj -- --require-ffmpeg
```

Expected: 검사 20의 파서·인자·고유 이름 조건 PASS.

- [ ] **Step 4: 장치 조회와 녹음 수명주기 구현**

`FFmpegMicrophoneRecorder`는 `System.Diagnostics.Process` 하나만 소유한다.

```csharp
public bool IsRecording => m_process is { HasExited: false };
public async Task<IReadOnlyList<AudioInputDevice>> ListAudioInputDevicesAsync(CancellationToken cancellationToken = default);
public async Task StartAsync(AudioInputDevice device, string outputDirectory, CancellationToken cancellationToken = default);
public async Task<string> StopAsync(CancellationToken cancellationToken = default);
public async ValueTask DisposeAsync();
```

구현 규칙:

- 생성자는 `ffmpegPath`가 존재하지 않으면 `FileNotFoundException`을 던진다.
- 장치 조회는 `-hide_banner -f avfoundation -list_devices true -i ""`를 실행하고 stderr를 끝까지 읽는다. FFmpeg 장치 나열의 비정상 종료 코드는 장치가 파싱됐으면 허용하고, 파싱된 실제 장치가 없으면 stderr를 포함해 실패한다.
- `StartAsync`는 출력 폴더가 없거나 다른 녹음이 활성 상태면 실패한다.
- 최종 경로는 `GetUniqueRecordingPath`, 임시 경로는 최종 경로에 `.part`를 붙인다.
- stderr 읽기 Task를 즉시 시작해 파이프 버퍼 교착을 막는다.
- `StopAsync`는 표준 입력에 `q`와 newline을 보내고 최대 5초 기다린다. 시간 초과 시 프로세스를 종료하고 실패한다.
- 종료 코드 0, 임시 파일 존재, 길이 > 0을 모두 확인한 뒤 `File.Move(temp, final)`한다.
- 실패 시 최종 파일을 만들지 않으며 임시 파일 삭제 실패는 예외 메시지에 경로를 포함한다.
- `DisposeAsync`는 활성 녹음이 있으면 `StopAsync`를 호출한다.

- [ ] **Step 5: 서비스 전체 검사와 정적 검증**

Run:

```bash
dotnet build VideoToText.Avalonia/VideoToText.Avalonia.csproj --no-restore
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj -- --require-ffmpeg
git diff --check -- VideoToText.Avalonia/Services/FFmpegMicrophoneRecorder.cs VideoToText.Checks/Program.cs
```

Expected: 빌드 경고/오류 0, 모든 검사 PASS, diff check 출력 없음. 커밋은 만들지 않는다.

### Task 3: 마이크 UI와 대기열 연동

**Files:**
- Modify: `VideoToText.Avalonia/ViewModels/MainWindowViewModel.cs`
- Modify: `VideoToText.Avalonia/Views/MainWindow.axaml`
- Modify: `VideoToText.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `VideoToText.Checks/Program.cs`

**Interfaces:**
- Consumes: `FFmpegMicrophoneRecorder`, `AudioInputDevice`
- Produces properties: `AudioInputDevices`, `SelectedAudioInputDevice`, `IsRecording`, `RecordingElapsed`, `RecordingButtonText`
- Produces commands: `RefreshAudioInputDevicesCommand`, `ToggleRecordingCommand`
- Produces: `StopRecordingForShutdownAsync() : Task`

- [ ] **Step 1: ViewModel 큐 연동 RED 검사 추가**

녹음 완료 콜백이 실제 큐 등록 메서드 하나를 통과하도록 `AddCompletedRecordingToQueue(string path)`를 내부 메서드로 두고 검사 21에서 다음을 확인한다.

```csharp
private static Task CheckRecordedFileQueueValidation()
{
    Assert(MainWindowViewModel.IsSupportedMediaPath("recording.m4a"), "확정 M4A가 대기열 입력에서 거부됨");
    Assert(!MainWindowViewModel.IsSupportedMediaPath("recording.m4a.part"), "미확정 part 파일이 대기열에 허용됨");
    return Task.CompletedTask;
}
```

Run:

```bash
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj --no-build -- --require-ffmpeg
```

Expected: `.m4a.part` 조건이 구현 전 실패하거나 검사 21 미등록으로 RED를 확인한다.

- [ ] **Step 2: ViewModel 녹음 상태와 명령 구현**

`MainWindowViewModel`에 다음 상태를 추가한다.

```csharp
public ObservableCollection<AudioInputDevice> AudioInputDevices { get; } = new();

[ObservableProperty] private AudioInputDevice? m_selectedAudioInputDevice;
[ObservableProperty] private bool m_isRecording;
[ObservableProperty] private string m_recordingElapsed = "00:00:00";
public string RecordingButtonText => IsRecording ? "녹음 중지" : "녹음 시작";
```

생성 시 기본 항목을 추가하고 FFmpeg 경로가 있으면 recorder를 만든다. 기존 `InitializeFfmpegPath`는 다음 반환 계약으로 바꿔 Xabe와 recorder가 같은 실행 파일을 사용하게 한다.

```csharp
private string? InitializeFfmpegPath(); // 설정한 ffmpeg 전체 경로 반환, 없으면 null
```

명령은 다음 동작을 가진다.

```csharp
[RelayCommand]
private async Task RefreshAudioInputDevicesAsync();

[RelayCommand]
private async Task ToggleRecordingAsync();

internal async Task StopRecordingForShutdownAsync();
```

- 새로고침은 recorder 결과로 컬렉션을 교체하고 기존 선택 인덱스를 유지한다.
- 시작은 `OutputPath`가 존재하는 폴더인지 검사한 뒤 선택 장치 또는 기본 장치로 `StartAsync`한다.
- `DispatcherTimer`를 1초 간격으로 실행해 `RecordingElapsed`를 갱신한다.
- 중지는 timer를 멈추고 `StopAsync`의 확정 M4A 경로를 `AddFileToQueue`에 전달한다.
- 성공 상태는 `녹음 저장 및 대기열 추가: 파일명`으로 표시한다.
- 실패 상태는 `[녹음 오류] 원인`으로 표시하고 작업을 추가하지 않는다.
- `OnIsRecordingChanged` 또는 명시적 알림으로 `RecordingButtonText` 변경을 통지한다.

- [ ] **Step 3: XAML 컨트롤 추가**

파일 추가 영역 아래에 한 행을 추가한다.

```xml
<Grid ColumnDefinitions="*, Auto, Auto, Auto" Margin="0,8,0,0">
    <ComboBox ItemsSource="{Binding AudioInputDevices}"
              SelectedItem="{Binding SelectedAudioInputDevice}"
              IsEnabled="{Binding !IsRecording}" />
    <Button Grid.Column="1" Content="장치 새로고침"
            Command="{Binding RefreshAudioInputDevicesCommand}"
            IsEnabled="{Binding !IsRecording}" Margin="5,0" />
    <TextBlock Grid.Column="2" Text="{Binding RecordingElapsed}"
               VerticalAlignment="Center" Margin="10,0" />
    <Button Grid.Column="3" Content="{Binding RecordingButtonText}"
            Command="{Binding ToggleRecordingCommand}"
            Background="#D13A55" Foreground="White" />
</Grid>
```

녹음 중에는 출력 경로 TextBox와 변경 버튼에도 `IsEnabled="{Binding !IsRecording}"`을 적용한다.

- [ ] **Step 4: 창 닫기에서 녹음 확정**

XAML Window에 `Closing="OnWindowClosing"`을 추가하고 코드비하인드에 재진입 가드를 둔다.

```csharp
private bool m_closeAfterRecordingStops;

private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
{
    if (m_closeAfterRecordingStops || DataContext is not MainWindowViewModel vm || !vm.IsRecording) return;
    e.Cancel = true;
    await vm.StopRecordingForShutdownAsync();
    m_closeAfterRecordingStops = true;
    Close();
}
```

중지 실패도 ViewModel 상태에 표시한 뒤 창은 닫는다. recorder의 실패 경로가 최종 파일을 작업으로 등록하지 않는 계약을 유지한다.

- [ ] **Step 5: GREEN과 UI 컴파일 확인**

Run:

```bash
dotnet build VideoToText.Avalonia/VideoToText.Avalonia.csproj --no-restore
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj -- --require-ffmpeg
```

Expected: XAML 컴파일 성공, 검사 21 포함 전부 PASS.

- [ ] **Step 6: Task 3 검수**

확인 항목:

- 파일 선택과 녹음 완료가 모두 `AddFileToQueue`를 호출한다.
- `.part` 파일은 큐에 들어가지 않는다.
- 녹음 중 출력 경로·장치 변경이 비활성화된다.
- 중지 성공 뒤 변환을 자동 시작하는 호출이 없다.
- 새 단일 구현 인터페이스나 패키지가 없다.

Run:

```bash
git diff --check -- VideoToText.Avalonia/ViewModels/MainWindowViewModel.cs VideoToText.Avalonia/Views/MainWindow.axaml VideoToText.Avalonia/Views/MainWindow.axaml.cs VideoToText.Checks/Program.cs
```

Expected: 출력 없음, 종료 코드 0. 커밋은 만들지 않는다.

### Task 4: macOS 권한, 문서, 전체 검증

**Files:**
- Modify: `build_macos.sh`
- Modify: `Doc/FEATURE_SPEC.md`
- Modify: `Doc/ARCHITECTURE.md`
- Modify: `Doc/PROJECT_STATUS.md`

**Interfaces:**
- Consumes: Task 1~3의 최종 UI·서비스·검사 계약
- Produces: 패키지 Info.plist의 `NSMicrophoneUsageDescription`

- [ ] **Step 1: plist 권한 RED 검사 추가**

`VideoToText.Checks/Program.cs` 검사 22에 정적 계약을 추가한다.

```csharp
private static Task CheckMacMicrophoneUsageDescription()
{
    string script = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "build_macos.sh"));
    Assert(script.Contains("NSMicrophoneUsageDescription", StringComparison.Ordinal), "마이크 사용 설명 키 누락");
    Assert(script.Contains("음성 녹음", StringComparison.Ordinal), "사용자용 마이크 권한 설명 누락");
    return Task.CompletedTask;
}
```

Run:

```bash
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj --no-build -- --require-ffmpeg
```

Expected: 키가 없어 검사 22 FAIL.

- [ ] **Step 2: 생성 Info.plist에 권한 설명 추가**

`build_macos.sh`의 fallback plist에 다음 두 항목을 추가한다.

```xml
<key>NSMicrophoneUsageDescription</key><string>음성 녹음 파일을 만들고 텍스트로 변환하기 위해 마이크를 사용합니다.</string>
```

저장소에 향후 `VideoToText.Avalonia/Info.plist`가 생기면 동일 키가 필요하다는 검증을 빌드 스크립트에 추가하지 않는다. 현재 소스에는 해당 파일이 없으므로 YAGNI로 유지한다.

- [ ] **Step 3: 문서 정합성 갱신**

세 문서에 다음 사실만 반영한다.

- MP3·M4A·WAV 가져오기와 기존 영상 입력 지원
- 기본/선택 마이크, 선택 경로 M4A 보존, 정상 종료 후 대기열 등록
- FFmpeg avfoundation, AAC, `NSMicrophoneUsageDescription`
- 실시간 변환·파형·편집은 미지원
- 자동검사 결과와 실제 마이크/권한/한국어 결과를 별도 상태로 표시

실행하지 않은 수동 검증을 PASS로 쓰지 않는다.

- [ ] **Step 4: 전체 빌드와 Checks**

Run:

```bash
dotnet build VideoToText/VideoToText.csproj --no-restore
dotnet build VideoToText.Avalonia/VideoToText.Avalonia.csproj --no-restore
dotnet build VideoToText.Checks/VideoToText.Checks.csproj --no-restore
dotnet run --project VideoToText.Checks/VideoToText.Checks.csproj --no-build -- --require-ffmpeg
bash -n build_macos.sh
python3 -m py_compile bundle_ffmpeg.py
git diff --check
```

Expected: 빌드 3종 경고/오류 0, Checks 22/22 PASS·0 SKIP, 정적 검사 종료 코드 0.

- [ ] **Step 5: 실제 macOS 패키징 검증**

Run:

```bash
./build_macos.sh
plutil -extract NSMicrophoneUsageDescription raw VideoToText.Avalonia/Publish/VideoTexter.app/Contents/Info.plist
codesign --verify --deep --strict VideoToText.Avalonia/Publish/VideoTexter.app
VideoToText.Avalonia/Publish/VideoTexter.app/Contents/MacOS/ffmpeg -hide_banner -h demuxer=avfoundation
unzip -tq VideoToText.Avalonia/Publish/VideoTexter_Standalone_osx-arm64.zip
```

Expected: 패키징 종료 코드 0, 한국어 마이크 설명 출력, 서명/avfoundation/ZIP 검사 성공.

- [ ] **Step 6: 수동 검증 상태 기록**

가능한 경우 다음을 실행하고 정확한 상태를 `Doc/PROJECT_STATUS.md`에 기록한다.

1. 기본 마이크 5초 녹음 → M4A 재생 → 대기열 등록
2. 선택 마이크 5초 녹음 → M4A 재생 → 대기열 등록
3. 실제 한국어 MP3·M4A → SRT/TXT 생성
4. 권한 거부, 녹음 중 장치 분리, 녹음 중 창 닫기

사용자 입력·장치·권한 때문에 실행하지 못한 항목은 `not_run` 또는 `미증명`으로 남긴다.

- [ ] **Step 7: 최종 독립 검수**

검수자는 전체 `git diff`와 다음을 읽기 전용으로 확인한다.

- 기존 영상과 새 음성 경로가 같은 대기열 소비자를 사용한다.
- 녹음 실패·중지 시간 초과·파일 충돌이 기존 파일을 잃게 하지 않는다.
- FFmpeg 프로세스 stderr가 비동기로 소비되어 교착하지 않는다.
- 창 닫기와 중복 녹음에서 프로세스가 남지 않는다.
- 문서가 수동 미검증을 완료로 과장하지 않는다.

새 Critical/Important finding이 0건일 때만 구현 완료로 보고한다. 커밋·푸시는 만들지 않는다.
