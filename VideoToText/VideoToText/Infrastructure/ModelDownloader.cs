using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace VideoToText.Infrastructure
{
    /// <summary>
    /// [설명]: Whisper 모델 파일을 온라인 저장소에서 다운로드하고 진행 상황을 보고하는 서비스 클래스입니다.
    /// </summary>
    public class ModelDownloader
    {
        private readonly HttpClient m_httpClient;

        public ModelDownloader()
        {
            m_httpClient = new HttpClient();
            // 타임아웃을 넉넉하게 설정 (모델 파일이 크기 때문)
            m_httpClient.Timeout = TimeSpan.FromMinutes(30);
        }

        /// <summary>
        /// [설명]: 지정된 URL에서 모델 파일을 다운로드하여 로컬 경로에 저장합니다.
        /// </summary>
        /// <param name="url">다운로드할 모델의 URL</param>
        /// <param name="destinationPath">저장할 로컬 파일 경로</param>
        /// <param name="onProgress">진행률(0.0 ~ 1.0)을 전달받을 콜백</param>
        public async Task DownloadFileAsync(string url, string destinationPath, Action<double> onProgress)
        {
            // 폴더가 없으면 생성
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var response = await m_httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            var totalReadBytes = 0L;
            var readCount = 0;

            while ((readCount = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await fileStream.WriteAsync(buffer, 0, readCount);
                totalReadBytes += readCount;

                if (totalBytes != -1)
                {
                    double progress = (double)totalReadBytes / totalBytes;
                    onProgress?.Invoke(progress);
                }
            }

            onProgress?.Invoke(1.0);
        }
    }
}
