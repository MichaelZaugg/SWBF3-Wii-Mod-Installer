// Services/DownloadService.cs
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using Avalonia.Threading;

namespace SWBF_C_build.Services;

public class DownloadService
{
    private static readonly HttpClient HttpClient = new();

    public async Task DownloadAndExtractAsync(string url, string destinationDir, Action<double>? progressCallback = null)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tmp");

        try
        {
            // 1. Download file with progress updates
            using (var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long totalBytes = response.Content.Headers.ContentLength ?? -1;

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        double progress = (double)totalRead / totalBytes * 70.0;
                        Dispatcher.UIThread.Post(() => progressCallback?.Invoke(progress));
                    }
                }
            }

            // 2. Extract Archive directly using SharpCompress
            progressCallback?.Invoke(85.0);
            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.OpenArchive(tempFile);
                archive.WriteToDirectory(destinationDir, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            });

            progressCallback?.Invoke(100.0);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}