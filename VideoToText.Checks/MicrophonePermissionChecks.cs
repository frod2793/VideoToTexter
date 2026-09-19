using System;
using System.IO;
using System.Threading.Tasks;

namespace VideoToText.Checks
{
    internal static class MicrophonePermissionChecks
    {
        internal static Task RunAsync()
        {
            string script = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "build_macos.sh"));
            Assert(script.Contains("NSMicrophoneUsageDescription", StringComparison.Ordinal), "마이크 사용 설명 키 누락");
            Assert(script.Contains("음성 녹음 파일을 만들고 텍스트로 변환하기 위해 마이크를 사용합니다.", StringComparison.Ordinal), "사용자용 마이크 권한 설명 누락");
            return Task.CompletedTask;
        }

        private static string GetRepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
