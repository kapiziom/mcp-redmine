using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace McpRedmine.Tests;

/// <summary>Starts the real server binary and drives it over stdio against an in-process fake Redmine.</summary>
public sealed class EndToEndTests : IDisposable
{
    private const string Secret = "E2E-SECRET-KEY-9f3a";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-e2e-" + Guid.NewGuid().ToString("N"));

    public EndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed record Seen(string Method, string Path, string? Key, string Body);

    private static async Task<(FakeRedmineServer Server, List<Seen> Requests)> StartFakeAsync()
    {
        var seen = new List<Seen>();
        var server = await FakeRedmineServer.StartAsync(app =>
        {
            app.MapGet("/users/current.json", (HttpContext c) =>
            {
                lock (seen) seen.Add(new Seen("GET", c.Request.Path, c.Request.Headers["X-Redmine-API-Key"], string.Empty));
                return Results.Text("""{"user":{"id":3,"login":"bot","firstname":"Bot","lastname":"B","api_key":"E2E-SECRET-KEY-9f3a"}}""", "application/json");
            });
            app.MapGet("/issues/42.json", () => Results.Text("""
                {"issue":{"id":42,"subject":"S","project":{"id":1,"name":"P"},"attachments":[
                  {"id":10,"filename":"shot.png","filesize":90,"content_type":"image/png","author":{"id":5,"name":"Anna Nowak"}},
                  {"id":9,"filename":"spec.docx","filesize":900,"content_type":"application/octet-stream","author":{"id":5,"name":"Anna Nowak"}}]}}
                """, "application/json"));
            app.MapGet("/attachments/download/10/shot.png", () => Results.Bytes(Ooxml.Png, "image/png"));
            app.MapGet("/attachments/download/9/spec.docx", () => Results.Bytes(Ooxml.Docx(Ooxml.Paragraph("Must log in.")), "application/octet-stream"));
            app.MapPost("/issues.json", async (HttpContext c) =>
            {
                var body = await new StreamReader(c.Request.Body).ReadToEndAsync();
                lock (seen) seen.Add(new Seen("POST", c.Request.Path, c.Request.Headers["X-Redmine-API-Key"], body));
                return Results.Text("""{"issue":{"id":501,"subject":"From e2e"}}""", "application/json", statusCode: 201);
            });
        });
        return (server, seen);
    }

    private string WriteConfig(string url) =>
        WriteConfigFile($$"""
            {"targets":[
              {"name":"ro","url":"{{url}}","allow_insecure_http":true,"api_key_env":"E2E_KEY"},
              {"name":"rw","url":"{{url}}","allow_insecure_http":true,"read_only":false,"api_key_env":"E2E_KEY"}]}
            """);

    private string WriteConfigFile(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static Dictionary<string, string> Env => new() { ["E2E_KEY"] = Secret };

    [Fact]
    public async Task Server_lists_every_tool_and_keeps_stdout_pure_protocol()
    {
        var (server, _) = await StartFakeAsync();
        await using var _ = server;
        using var mcp = await McpProcess.StartAsync(WriteConfig(server.BaseUrl), Env);

        var tools = (await mcp.RequestAsync("tools/list")).GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order().ToList();

        Assert.Equal(
            ["add_issue_note", "create_issue", "get_attachment", "get_issue", "list_enumerations", "list_projects", "list_targets", "log_time", "search_issues", "whoami"],
            names);

        // Every stdout line must be a JSON-RPC message; anything else would corrupt the protocol stream.
        foreach (var line in mcp.StdoutLines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    [Fact]
    public async Task Write_to_a_read_only_target_is_rejected_and_nothing_reaches_redmine()
    {
        var (server, seen) = await StartFakeAsync();
        await using var _ = server;
        using var mcp = await McpProcess.StartAsync(WriteConfig(server.BaseUrl), Env);

        var (isError, text) = await mcp.CallToolAsync("create_issue", new { target = "ro", project_id = "p", subject = "nope" });

        Assert.True(isError);
        Assert.Contains("read-only", text);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Write_to_a_writable_target_goes_through_with_the_key_in_the_header()
    {
        var (server, seen) = await StartFakeAsync();
        await using var _ = server;
        using var mcp = await McpProcess.StartAsync(WriteConfig(server.BaseUrl), Env);

        var (isError, text) = await mcp.CallToolAsync("create_issue", new { target = "rw", project_id = "p", subject = "From e2e" });

        Assert.False(isError, text);
        Assert.Equal(501, JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt32());
        var request = Assert.Single(seen);
        Assert.Equal(Secret, request.Key);
        Assert.Contains("From e2e", request.Body);
    }

    [Fact]
    public async Task Attachments_reach_the_agent_over_stdio_as_an_image_and_as_extracted_text()
    {
        var (server, _) = await StartFakeAsync();
        await using var _ = server;
        using var mcp = await McpProcess.StartAsync(WriteConfig(server.BaseUrl), Env);

        var image = await mcp.CallToolRawAsync("get_attachment", new { target = "ro", issue_id = 42, attachment_id = 10 });
        var docx = await mcp.CallToolRawAsync("get_attachment", new { target = "ro", issue_id = 42, attachment_id = 9 });

        var blocks = image.GetProperty("content").EnumerateArray().ToList();
        var picture = Assert.Single(blocks, b => b.GetProperty("type").GetString() == "image");
        Assert.Equal("image/png", picture.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(Ooxml.Png), picture.GetProperty("data").GetString());
        Assert.DoesNotContain("Anna", blocks[0].GetProperty("text").GetString());

        var texts = docx.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("text").GetString()).ToList();
        Assert.Contains(texts, t => t!.Contains("Must log in.", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, string.Join('\n', mcp.StdoutLines));
    }

    [Fact]
    public async Task Missing_target_with_several_configured_is_a_tool_error_not_a_crash()
    {
        var (server, seen) = await StartFakeAsync();
        await using var _ = server;
        using var mcp = await McpProcess.StartAsync(WriteConfig(server.BaseUrl), Env);

        var (isError, text) = await mcp.CallToolAsync("whoami", new { });

        Assert.True(isError);
        Assert.Contains("ro, rw", text);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task The_api_key_never_appears_on_stdout_or_stderr_even_though_redmine_echoes_it()
    {
        var (server, _) = await StartFakeAsync();
        await using var _ = server;
        using var mcp = await McpProcess.StartAsync(WriteConfig(server.BaseUrl), Env);

        var (isError, text) = await mcp.CallToolAsync("whoami", new { target = "ro" });
        await mcp.CallToolAsync("whoami", new { target = "ghost" });

        Assert.False(isError, text);
        Assert.Equal(3, JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt32());
        Assert.DoesNotContain("bot", text);
        Assert.DoesNotContain(Secret, string.Join('\n', mcp.StdoutLines));
        Assert.DoesNotContain(Secret, mcp.Stderr);
    }

    [Fact]
    public async Task Invalid_config_stops_the_server_with_a_readable_message()
    {
        var config = WriteConfigFile("""{"targets":[{"name":"a","url":"http://plain.example.com"}]}""");
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(McpProcess.FindServerDll());
        info.Environment["REDMINE_MCP_CONFIG"] = config;
        using var process = System.Diagnostics.Process.Start(info)!;

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("http://", stderr);
        Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
    }
}
