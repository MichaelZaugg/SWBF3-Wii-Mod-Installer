// Services/ConsoleRedirector.cs
using System;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace SWBF_C_build.Services;

public class ConsoleRedirector : TextWriter
{
    private readonly TextWriter _originalOutput;
    private readonly Action<string> _logCallback;

    public ConsoleRedirector(TextWriter originalOutput, Action<string> logCallback)
    {
        _originalOutput = originalOutput;
        _logCallback = logCallback;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        // Write to terminal as usual
        _originalOutput.WriteLine(value);

        if (string.IsNullOrEmpty(value)) return;

        // Dispatch to Avalonia UI thread
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _logCallback?.Invoke($"[{DateTime.Now:HH:mm:ss}] {value}\n");
        });
    }

    public override void Write(string? value)
    {
        _originalOutput.Write(value);
        if (!string.IsNullOrEmpty(value))
        {
            Dispatcher.UIThread.InvokeAsync(() => _logCallback?.Invoke(value));
        }
    }
}