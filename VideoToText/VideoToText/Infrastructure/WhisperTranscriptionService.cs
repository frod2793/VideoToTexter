using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using VideoToText.Core;

namespace VideoToText.Infrastructure
{
    public class WhisperTranscriptionService : ITranscriptionService, IDisposable
    {
        private readonly string m_modelPath;
        private WhisperFactory? m_factory;
        
        private readonly Queue<string> m_history = new Queue<string>();
        private const int MAX_HISTORY_COUNT = 5;

        public WhisperTranscriptionService(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentNullException(nameof(modelPath));
            m_modelPath = modelPath;
        }

        public async IAsyncEnumerable<TranscriptionResultDTO> TranscribeAsync(
            string audioPath, 
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!File.Exists(m_modelPath)) throw new FileNotFoundException("모델 파일 부재");
            if (m_factory == null) m_factory = WhisperFactory.FromPath(m_modelPath);

            using var processor = m_factory.CreateBuilder()
                .WithLanguage("ko") 
                .WithThreads(1)     
                .Build();

            byte[] wavData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            using var ms = new MemoryStream(wavData);
            
            m_history.Clear();
            
            await foreach (var segment in processor.ProcessAsync(ms, cancellationToken))
            {
                string text = segment.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(text)) continue;

                if (IsRedundant(text))
                {
                    continue;
                }

                UpdateHistory(text);

                yield return new TranscriptionResultDTO
                {
                    Text = text,
                    Start = segment.Start,
                    End = segment.End,
                    Probability = segment.Probability
                };
            }
        }

        private bool IsRedundant(string text)
        {
            return m_history.Contains(text);
        }

        private void UpdateHistory(string text)
        {
            m_history.Enqueue(text);
            if (m_history.Count > MAX_HISTORY_COUNT)
            {
                m_history.Dequeue();
            }
        }

        public void Dispose()
        {
            m_factory?.Dispose();
            m_factory = null;
        }
    }
}
