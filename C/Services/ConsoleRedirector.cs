// Services/ConsoleRedirector.cs
using System;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace SWBF_C_build.Services;

/// <summary>
/// Sends Console output to both the original writer (terminal) and a UI callback.
/// Installed in MainWindow with Console.SetOut / Console.SetError.
/// </summary>
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
        Dispatch($"[{DateTime.Now:HH:mm:ss}] {value}\n");
    }

    public override void Write(string? value)
    {
        _originalOutput.Write(value);
        if (!string.IsNullOrEmpty(value))
            Dispatch(value);
    }

    // TextWriter's other overloads (Write(char), WriteLine() with no text, ...) end up here;
    // without this override they'd be silently dropped.
    public override void Write(char value)
    {
        _originalOutput.Write(value);
        Dispatch(value.ToString());
    }

    public override void Flush() => _originalOutput.Flush();

    private void Dispatch(string text) =>
        Dispatcher.UIThread.Post(() => _logCallback?.Invoke(text));
}