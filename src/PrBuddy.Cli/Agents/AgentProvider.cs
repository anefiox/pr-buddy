using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PrBuddy.Core;

namespace PrBuddy.Cli.Agents;

sealed class AgentProvider : IDisposable
{
    const int MaxResponseChars = 256_000;
    readonly string _provider;
    readonly string _model;
    readonly string _schema;
    readonly HttpClient? _http;
    readonly Uri? _endpoint;
    readonly string? _apiKey;

    public AgentProvider(string provider, string model, string schema)
    {
        _provider = provider;
        _model = model;
        _schema = schema;
        if (provider == "openrouter")
        {
            _endpoint = new Uri("https://openrouter.ai/api/v1/chat/completions");
            _apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new PrBuddyException("Set OPENROUTER_API_KEY before running an OpenRouter review.");
        }
        else if (provider == "local")
        {
            var baseUrl = Environment.GetEnvironmentVariable("PRBUDDY_LOCAL_BASE_URL") ?? "http://127.0.0.1:11434/v1";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new PrBuddyException("PRBUDDY_LOCAL_BASE_URL must be an HTTP(S) API base URL without credentials, query or fragment.");
            _endpoint = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
            _apiKey = Environment.GetEnvironmentVariable("PRBUDDY_LOCAL_API_KEY");
        }

        if (_endpoint is not null)
        {
            if (!string.IsNullOrEmpty(_apiKey) && _apiKey.Any(c => c <= ' ' || c >= 127))
                throw new PrBuddyException("The API key must contain printable ASCII characters with no spaces.");
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = provider != "local" })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    public Task<string> DecideAsync(string instructions, string input, CancellationToken cancellation) =>
        _http is null ? RunCliAsync(instructions, input, cancellation) : CallApiAsync(instructions, input, cancellation);

    async Task<string> CallApiAsync(string instructions, string input, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = input } },
            response_format = new { type = "json_object" },
            max_tokens = 4096,
            stream = false
        });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new PrBuddyException($"The {_provider} API returned HTTP {(int)response.StatusCode}. Check the model and credentials.");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation), Encoding.UTF8);
            using var json = JsonDocument.Parse(await ReadResponseAsync(reader, cancellation));
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0] is { ValueKind: JsonValueKind.Object } choice &&
                choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String &&
                finish.GetString() == "stop" && choice.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString()!;
            throw new PrBuddyException($"The {_provider} API did not return a complete review decision.");
        }
        catch (HttpRequestException)
        {
            throw new PrBuddyException($"Could not reach the {_provider} API.");
        }
        catch (IOException)
        {
            throw new PrBuddyException($"Could not read the {_provider} API response.");
        }
    }

    async Task<string> RunCliAsync(string instructions, string input, CancellationToken cancellation)
    {
        var executable = FindNativeCli();
        var workdir = Directory.CreateTempSubdirectory("prbuddy-agent-");
        try
        {
            var schemaPath = Path.Combine(workdir.FullName, "schema.json");
            var resultPath = Path.Combine(workdir.FullName, "result.json");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workdir.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            string[] args;
            if (_provider == "codex")
            {
                await File.WriteAllTextAsync(schemaPath, _schema, cancellation);
                args = ["--ask-for-approval", "never", "exec", "--sandbox", "read-only", "--skip-git-repo-check",
                    "--ephemeral", "--ignore-user-config", "--ignore-rules", "--disable", "shell_tool",
                    "--disable", "unified_exec", "--disable", "multi_agent", "--disable", "multi_agent_v2",
                    "--disable", "hooks", "--disable", "plugins", "--disable", "remote_plugin", "--disable", "skill_search",
                    "-c", "web_search=\"disabled\"", "-c", "project_doc_max_bytes=0",
                    "-c", "cli_auth_credentials_store=\"auto\"",
                    "--model", _model, "--output-schema", schemaPath,
                    "--output-last-message", resultPath, "-"];
            }
            else
            {
                args = ["--print", "--safe-mode", "--strict-mcp-config", "--tools", "",
                    "--disallowedTools", "mcp__*", "--max-turns", "2",
                    "--no-session-persistence", "--output-format", "json", "--json-schema", _schema,
                    "--model", _model, "--system-prompt", instructions];
            }
            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new PrBuddyException($"Could not start {_provider}.");
            try
            {
                var output = ReadResponseAsync(process.StandardOutput, cancellation);
                var drainErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation);
                var prompt = _provider == "codex" ? instructions + "\n\n" + input : input;
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellation);
                process.StandardInput.Close();
                await Task.WhenAll(process.WaitForExitAsync(cancellation), output, drainErrors);
                if (process.ExitCode != 0)
                    throw new PrBuddyException($"{_provider} exited with code {process.ExitCode}. Check its version and sign-in.");
                if (_provider == "codex")
                {
                    if (!File.Exists(resultPath))
                        throw new PrBuddyException("Codex did not write a review decision.");
                    using var reader = File.OpenText(resultPath);
                    return await ReadResponseAsync(reader, cancellation);
                }
                using var json = JsonDocument.Parse(await output);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.False &&
                    json.RootElement.TryGetProperty("structured_output", out var decision) &&
                    decision.ValueKind == JsonValueKind.Object)
                    return decision.GetRawText();
                throw new PrBuddyException("Claude Code did not return a complete review decision.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { } // It exited before the kill arrived.
                    await process.WaitForExitAsync();
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new PrBuddyException($"Could not run {_provider}. Check the native CLI installation and sign-in.");
        }
        finally
        {
            workdir.Delete(recursive: true);
        }
    }

    string FindNativeCli()
    {
        var filename = OperatingSystem.IsWindows() ? _provider + ".exe" : _provider;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, filename);
            if (File.Exists(candidate)) return candidate;
        }
        throw new PrBuddyException($"Install the native {_provider} CLI on PATH and sign in with '{_provider} " +
            (_provider == "codex" ? "login'." : "auth login'."));
    }

    static async Task<string> ReadResponseAsync(TextReader reader, CancellationToken cancellation)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        var overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            if (output.Length + read > MaxResponseChars) overflow = true;
            if (!overflow) output.Append(buffer, 0, read);
        }
        if (overflow) throw new PrBuddyException("The agent exceeded the response size limit.");
        return output.ToString();
    }

    public void Dispose() => _http?.Dispose();
}
