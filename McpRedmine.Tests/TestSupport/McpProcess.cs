using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace McpRedmine.Tests.TestSupport;

/// <summary>Runs the real server binary and talks JSON-RPC over its stdin/stdout, like an MCP client would.</summary>
public sealed class McpProcess : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _stdout = [];
    private readonly StringBuilder _stderr = new();
    private readonly List<string> _allStdout = [];
    private int _nextId;

    private McpProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_allStdout)
                _allStdout.Add(e.Data);
            _stdout.Add(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_stderr)
                    _stderr.AppendLine(e.Data);
        };
    }

    public IReadOnlyList<string> StdoutLines
    {
        get { lock (_allStdout) return [.. _allStdout]; }
    }

    /// <summary>The server's answer to <c>initialize</c> (capabilities, instructions).</summary>
    public JsonElement InitializeResult { get; private set; }

    public string Stderr
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static async Task<McpProcess> StartAsync(string configPath, IDictionary<string, string>? environment = null)
    {
        var dll = FindServerDll();
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        info.ArgumentList.Add(dll);
        info.Environment["REDMINE_MCP_CONFIG"] = configPath;
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            info.Environment[key] = value;

        var process = Process.Start(info)!;
        var session = new McpProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        session.InitializeResult = await session.RequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "tests", version = "0" },
        });
        await session.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        return session;
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters = null, int timeoutSeconds = 30)
    {
        var id = Interlocked.Increment(ref _nextId);
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!_stdout.TryTake(out var line, 250))
                continue;

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                return document.RootElement.Clone();
        }

        throw new TimeoutException($"No response to '{method}' within {timeoutSeconds}s. Stderr: {Stderr}");
    }

    public async Task<(bool IsError, string Text)> CallToolAsync(string name, object arguments, int timeoutSeconds = 30)
    {
        var response = await RequestAsync("tools/call", new { name, arguments }, timeoutSeconds);
        var result = response.GetProperty("result");
        var text = string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));
        return (result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);
    }

    /// <summary>The whole <c>result</c> object, for tools that return more than text (images).</summary>
    public async Task<JsonElement> CallToolRawAsync(string name, object arguments, int timeoutSeconds = 30) =>
        (await RequestAsync("tools/call", new { name, arguments }, timeoutSeconds)).GetProperty("result");

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(5000))
                _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _process.Dispose();
            _stdout.Dispose();
        }
    }

    private async Task SendAsync(object message)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await _process.StandardInput.FlushAsync();
    }

    public static string FindServerDll()
    {
        // The test project references the server project, so its own build output is the freshest copy.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "McpRedmine.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name; // bin/<Configuration>/net10.0
        var candidates = Directory.GetFiles(Path.Combine(root, "McpRedmine", "bin", configuration, "net10.0"), "McpRedmine.dll", SearchOption.AllDirectories)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        Assert.NotEmpty(candidates);
        return candidates[0];
    }
}
