// Services/DownloadService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using Avalonia.Threading;
using SWBF_C_build;

namespace SWBF_C_build.Services;

public class DownloadService
{
    private static readonly HttpClient HttpClient = new();

    public async Task DownloadAndExtractAsync(string url, string targetDir, Action<double, string> onProgress)
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

                var stopwatch = Stopwatch.StartNew();
                long lastUpdateBytes = 0;
                long lastUpdateTime = 0;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        // Update UI every 500ms to calculate speed accurately
                        if (stopwatch.ElapsedMilliseconds - lastUpdateTime > 500)
                        {
                            double progress = (double)totalRead / totalBytes * 70.0; // Download takes up to 70%

                            long bytesSinceLastUpdate = totalRead - lastUpdateBytes;
                            double secondsSinceLastUpdate = (stopwatch.ElapsedMilliseconds - lastUpdateTime) / 1000.0;
                            long bytesPerSecond = (long)(bytesSinceLastUpdate / secondsSinceLastUpdate);

                            int downloadPercent = (int)((double)totalRead / totalBytes * 100);
                            string speedText = $"{FileSizeFormatter.FormatBytes(bytesPerSecond)}/s";
                            string statusMessage = $"Downloading . . . {downloadPercent}% ({speedText})";

                            Dispatcher.UIThread.Post(() => onProgress?.Invoke(progress, statusMessage));

                            lastUpdateBytes = totalRead;
                            lastUpdateTime = stopwatch.ElapsedMilliseconds;
                        }
                    }
                }
            }

            // 2. Extract Archive directly using SharpCompress loop to track progress
            Dispatcher.UIThread.Post(() => onProgress?.Invoke(70.0, "Extracting . . . (0%)"));
            
            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.OpenArchive(tempFile);
                
                // Filter out directory entries (WriteToDirectory creates them automatically)
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                int totalEntries = entries.Count;
                int extracted = 0;

                var extractStopwatch = Stopwatch.StartNew();
                long lastExtractTime = 0;

                foreach (var entry in entries)
                {
                    entry.WriteToDirectory(targetDir, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                    extracted++;
                    
                    // Throttle UI updates to every 100ms so the UI stays smooth
                    if (extractStopwatch.ElapsedMilliseconds - lastExtractTime > 100 || extracted == totalEntries)
                    {
                        // Map the extraction phase to the remaining 30% of the overall progress bar
                        double progress = 70.0 + ((double)extracted / totalEntries * 30.0);
                        int extractPercent = (int)((double)extracted / totalEntries * 100);
                        
                        Dispatcher.UIThread.Post(() => onProgress?.Invoke(progress, $"Extracting . . . ({extractPercent}%)"));
                        
                        lastExtractTime = extractStopwatch.ElapsedMilliseconds;
                    }
                }
            });

            // 3. Finish
            Dispatcher.UIThread.Post(() => onProgress?.Invoke(100.0, "Install Complete!"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}