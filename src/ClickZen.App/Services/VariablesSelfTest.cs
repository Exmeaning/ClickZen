using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClickZen.Core.Automation;
using ClickZen.Core.Settings;
using ClickZen.Core.Variables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for the variables page and sync service (<c>--selftest-variables</c>): declares
/// synced variables in the open scheme, enables the service on a free port with a token, then talks to it
/// over real TCP like an external program (hello, wrong token, subscribe, set, push) and checks the
/// Variables page shows the live values. Needs no device. Exit code 0 = pass, 2 = fail.
/// </summary>
internal sealed class VariablesSelfTest
{
    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public VariablesSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exit = 2;
        try
        {
            var automation = _services.GetRequiredService<AutomationService>();
            var sync = _services.GetRequiredService<VariableSyncService>();
            var settings = _services.GetRequiredService<SettingsService>();

            // 1. Declare variables in the scheme (as the page would).
            automation.Scheme.Variables.Add(new VariableDefinition { Name = "score", Type = VariableType.Int, Initial = 5, Scope = VariableScope.Global, Sync = SyncDirection.Both });
            automation.Scheme.Variables.Add(new VariableDefinition { Name = "status", Type = VariableType.String, Initial = "idle", Scope = VariableScope.Global, Sync = SyncDirection.Send });
            automation.Scheme.Variables.Add(new VariableDefinition { Name = "hidden", Type = VariableType.Int, Initial = 1, Scope = VariableScope.Global });
            automation.VariablesEdited();

            // 2. Enable the service on a free port with a token.
            var port = FreePort();
            settings.Update(s => s.Sync.Port = port);
            sync.Token = "selftest-token";
            await sync.SetEnabledAsync(true);
            if (!sync.IsRunning)
            {
                _log.LogError("Variables self-test: server did not start ({Error})", sync.LastError);
                return;
            }

            _window.NavigateTo("variables");
            await Task.Delay(500);

            // 3. Wrong token is refused.
            using (var bad = await Client.ConnectAsync(port))
            {
                var r = await bad.RequestAsync(new { type = "hello", token = "nope", protocol = 2 });
                if (r.GetProperty("type").GetString() != "error" || r.GetProperty("code").GetString() != "auth")
                {
                    _log.LogError("Variables self-test: wrong token was not rejected: {Reply}", r);
                    return;
                }
            }

            // 4. Real session: hello, getAll, subscribe, set, push.
            using var c = await Client.ConnectAsync(port);
            var welcome = await c.RequestAsync(new { type = "hello", token = "selftest-token", client = "selftest", protocol = 2 });
            if (welcome.GetProperty("type").GetString() != "welcome")
            {
                _log.LogError("Variables self-test: hello failed: {Reply}", welcome);
                return;
            }

            var all = await c.RequestAsync(new { type = "getAll" });
            var names = all.GetProperty("values").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            _log.LogInformation("Variables self-test: getAll -> {Names}", string.Join(",", names));
            if (!names.SequenceEqual(["score", "status"]))
            {
                _log.LogError("Variables self-test: unexpected readable variables");
                return;
            }

            var sub = await c.RequestAsync(new { type = "subscribe", names = new[] { "status" } });
            var initial = await c.ReadAsync();
            if (sub.GetProperty("type").GetString() != "ok" || initial.GetProperty("value").GetString() != "idle")
            {
                _log.LogError("Variables self-test: subscribe failed: {Sub} {Initial}", sub, initial);
                return;
            }

            var set = await c.RequestAsync(new { type = "set", name = "score", value = "42" });
            var readOnly = await c.RequestAsync(new { type = "set", name = "status", value = "x" });
            if (set.GetProperty("type").GetString() != "ok" || readOnly.GetProperty("code").GetString() != "read_only"
                || sync.Globals.Get("score") != VariableValue.FromInt(42) || sync.Globals.Get("score").Type != VariableType.Int)
            {
                _log.LogError("Variables self-test: set semantics wrong: {Set} {ReadOnly} score={Score}", set, readOnly, sync.Globals.Get("score"));
                return;
            }

            // A change made in the app (as the page's value box does) is pushed to the subscriber.
            sync.Globals.Set("status", "battle", VariableChangeSource.User);
            var pushed = await c.ReadAsync();
            if (pushed.GetProperty("type").GetString() != "changed" || pushed.GetProperty("value").GetString() != "battle"
                || pushed.GetProperty("source").GetString() != "user")
            {
                _log.LogError("Variables self-test: push missing: {Pushed}", pushed);
                return;
            }

            // 5. The page shows the client and the live value.
            await Task.Delay(800);
            var page = _window.Frame.Content as Views.VariablesPage;
            var row = page?.ViewModel.Rows.FirstOrDefault(r => r.Name == "score");
            var clients = page?.ViewModel.Clients.Count ?? 0;
            _log.LogInformation("Variables self-test: page rows {Rows}, score shown as {Value}, clients {Clients}, status bar \"{Status}\"",
                page?.ViewModel.Rows.Count, row?.CurrentValue, clients, sync.IsRunning);
            if (row?.CurrentValue != "42" || clients != 1)
            {
                _log.LogError("Variables self-test failed: page did not reflect the live state");
                return;
            }

            await sync.SetEnabledAsync(false);
            _log.LogInformation("Variables self-test passed");
            exit = 0;
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Variables self-test crashed");
        }
        finally
        {
            Environment.Exit(exit);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;

        private Client(TcpClient tcp)
        {
            _tcp = tcp;
            _reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
        }

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
            return new Client(tcp);
        }

        public async Task<JsonElement> RequestAsync(object message)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
            await _tcp.GetStream().WriteAsync(bytes);
            return await ReadAsync();
        }

        public async Task<JsonElement> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Connection closed.");
            return JsonDocument.Parse(line).RootElement.Clone();
        }

        public void Dispose() => _tcp.Dispose();
    }
}
