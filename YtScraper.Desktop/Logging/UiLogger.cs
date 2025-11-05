using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace YtScraper.Desktop.Logging;

public sealed class UiLogger : ILogger
{
    private readonly TextBox _target;
    private readonly Dispatcher _dispatcher;

    public UiLogger(TextBox target, Dispatcher dispatcher)
    {
        _target = target;
        _dispatcher = dispatcher;
    }

    public void LogInformation(string message) => Append("INFO", message);

    public void LogError(string message) => Append("ERROR", message);

    private void Append(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timestamp}] {level}: {message}";

        if (_dispatcher.CheckAccess())
        {
            WriteLine(line);
        }
        else
        {
            _dispatcher.Invoke(() => WriteLine(line));
        }
    }

    private void WriteLine(string line)
    {
        if (_target.Text.Length > 0)
        {
            _target.AppendText(Environment.NewLine);
        }

        _target.AppendText(line);
        _target.ScrollToEnd();
    }
}
