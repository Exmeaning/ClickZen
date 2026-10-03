using System.Text;
using AdvancedSharpAdbClient.Receivers;

namespace ClickZen.Device.Adb;

/// <summary>Collects every output line of a shell command (nothing is truncated).</summary>
internal sealed class CollectingReceiver : IShellOutputReceiver
{
    private readonly StringBuilder _sb = new();
    private bool _any;

    public string Output => _sb.ToString();

    public bool AddOutput(string line)
    {
        if (_any)
        {
            _sb.Append('\n');
        }

        _sb.Append(line);
        _any = true;
        return true;
    }

    public Task<bool> AddOutputAsync(string line, CancellationToken cancellationToken) => Task.FromResult(AddOutput(line));

    public void Flush()
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Forwards each line to a callback. An exception thrown by the callback stops the command and is rethrown to
/// the caller unchanged (instead of being wrapped by the library as "shell command unresponsive").
/// </summary>
internal sealed class CallbackReceiver(Action<string> onLine) : IShellOutputReceiver
{
    public Exception? CallbackError { get; private set; }

    public bool AddOutput(string line)
    {
        if (CallbackError is not null)
        {
            return false;
        }

        try
        {
            onLine(line);
            return true;
        }
        catch (Exception ex)
        {
            CallbackError = ex;
            return false;
        }
    }

    public Task<bool> AddOutputAsync(string line, CancellationToken cancellationToken) => Task.FromResult(AddOutput(line));

    public void Flush()
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
