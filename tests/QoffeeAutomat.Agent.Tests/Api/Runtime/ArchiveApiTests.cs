using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Api.Runtime;

/// <summary>Export, import and the diagnostics bundle of the management API (T214).</summary>
[Trait("Category", "ApiRuntime")]
public sealed class ArchiveApiTests
{
    private const string ExportPath = "/api/v1/config/export";
    private const string ImportPath = "/api/v1/config/import";

    [Fact]
    public async Task Export_followed_by_import_into_an_empty_data_directory_reproduces_the_configuration_without_secrets()
    {
        await using ApiTestHost source = await ApiTestHost.StartAsync();
        await source.CreateCommandAsync(ApiSamples.Command("say_hello"));
        await source.CreateCommandAsync(ApiSamples.CommandWithLevel("set_level"));
        await CreateSensorAsync(source, "app_running", "notepad");
        await CreateSensorAsync(source, "other_app", "calc");
        using (HttpResponseMessage first = await source.PutAsync("/api/v1/scripts/hello.ps1", "{\"content\":\"Write-Output 'hello'\\n\"}"))
        {
            Assert.True(first.IsSuccessStatusCode);
        }

        using (HttpResponseMessage second = await source.PutAsync("/api/v1/scripts/check.sh", "{\"content\":\"echo check\\n\"}"))
        {
            Assert.True(second.IsSuccessStatusCode);
        }

        string metric = SimulatedMetricProvider.CpuId;
        using (HttpResponseMessage hide = await source.PutAsync("/api/v1/metrics/" + metric, "{\"exposeToHa\":false,\"intervalSeconds\":5}"))
        {
            Assert.True(hide.IsSuccessStatusCode);
        }

        byte[] archive = await ExportAsync(source);
        Dictionary<string, byte[]> entries = ReadEntries(archive);
        Assert.Contains("config/commands/say_hello.json", entries.Keys);
        Assert.Contains("config/commands/set_level.json", entries.Keys);
        Assert.Contains("config/sensors/app_running.json", entries.Keys);
        Assert.Contains("config/sensors/other_app.json", entries.Keys);
        Assert.Contains("config/metrics.json", entries.Keys);
        Assert.Contains("scripts/hello.ps1", entries.Keys);
        Assert.Contains("scripts/check.sh", entries.Keys);
        Assert.Contains("export.json", entries.Keys);
        Assert.DoesNotContain(entries.Keys, name => name.StartsWith("secrets", StringComparison.OrdinalIgnoreCase));
        await AssertNoSecretsAsync(source, entries, "the export");

        // A fresh agent with an empty data directory: nothing of the configuration exists yet.
        await using ApiTestHost target = await ApiTestHost.StartAsync();
        Assert.Empty((await ListAsync(target, "/api/v1/commands", "commands")));
        Assert.Empty((await ListAsync(target, "/api/v1/sensors", "sensors")));
        Assert.Empty((await ListAsync(target, "/api/v1/scripts", "scripts")));

        using HttpResponseMessage imported = await target.SendAsync(HttpMethod.Post, ImportPath, body: archive);
        JsonNode result = await ApiAssert.JsonAsync(imported);
        Assert.Equal(2, result["commands"]!.GetValue<int>());
        Assert.Equal(2, result["sensors"]!.GetValue<int>());
        Assert.Equal(2, result["scripts"]!.GetValue<int>());
        Assert.True(result["metrics"]!.GetValue<bool>());

        Assert.Equal(["say_hello", "set_level"], await IdsAsync(target, "/api/v1/commands", "commands", "id"));
        Assert.Equal(["app_running", "other_app"], await IdsAsync(target, "/api/v1/sensors", "sensors", "id"));
        Assert.Equal(["check.sh", "hello.ps1"], await IdsAsync(target, "/api/v1/scripts", "scripts", "name"));
        Assert.Equal("Write-Output 'hello'\n", await File.ReadAllTextAsync(Path.Combine(target.DataPath, "scripts", "hello.ps1")));
        using (HttpResponseMessage described = await target.GetAsync("/api/v1/metrics/" + metric))
        {
            JsonNode info = await ApiAssert.JsonAsync(described);
            Assert.False(info["exposeToHa"]!.GetValue<bool>());
            Assert.Equal(5d, info["intervalSeconds"]!.GetValue<double>());
        }

        // The second export holds the same documents and scripts, byte for byte. The settings belong to one PC and are
        // not imported by default, and export.json carries the time of the export.
        Dictionary<string, byte[]> again = ReadEntries(await ExportAsync(target));
        foreach (string name in new[] { "config/settings.json", "export.json" })
        {
            entries.Remove(name);
            again.Remove(name);
        }

        Assert.Equal(entries.Keys.Order(StringComparer.Ordinal), again.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, byte[] content) in entries)
        {
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                JsonNode? expected = StripUpdatedAt(JsonNode.Parse(content));
                JsonNode? actual = StripUpdatedAt(JsonNode.Parse(again[name]));
                Assert.True(JsonNode.DeepEquals(expected, actual), "The entry differs after the import: " + name);
            }
            else
            {
                Assert.True(content.AsSpan().SequenceEqual(again[name]), "The entry differs after the import: " + name);
            }
        }
    }

    private static JsonNode? StripUpdatedAt(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("updatedAt");
            if (obj["metadata"] is JsonObject meta)
            {
                meta.Remove("updatedAt");
            }
        }

        return node;
    }

    [Fact]
    public async Task An_import_with_an_invalid_document_is_rejected_and_nothing_is_applied()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        // Everything but one document is fine; the valid parts must not be applied either.
        byte[] archive = BuildArchive(
            ("config/commands/good_one.json", ApiSamples.Command("good_one")),
            ("config/commands/broken.json", "{\"id\":\"broken\",\"name\":\"\",\"action\":{\"type\":\"nonsense\"}}"),
            ("config/sensors/fine.json", ApiSamples.Sensor("fine", "notepad")),
            ("scripts/fine.ps1", "Write-Output 'fine'\n"));

        using HttpResponseMessage rejected = await host.SendAsync(HttpMethod.Post, ImportPath, body: archive);
        JsonNode problem = await ApiAssert.ProblemAsync(rejected, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains(ApiAssert.ErrorPaths(problem), path => path.Contains("broken.json", StringComparison.Ordinal));
        await AssertNothingAppliedAsync(host);

        // An entry that does not belong in the archive, a document whose id differs from its entry name, and a file
        // that is not a zip are refused in the same way.
        byte[] mismatch = BuildArchive(("config/commands/one_name.json", ApiSamples.Command("another_name")));
        using HttpResponseMessage idMismatch = await host.SendAsync(HttpMethod.Post, ImportPath, body: mismatch);
        JsonNode mismatchProblem = await ApiAssert.ProblemAsync(idMismatch, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains(ApiAssert.ErrorPaths(mismatchProblem), path => path.EndsWith("/id", StringComparison.Ordinal));

        byte[] stranger = BuildArchive(("config/commands/good_one.json", ApiSamples.Command("good_one")), ("secrets/secrets.json", "{}"));
        using HttpResponseMessage unexpected = await host.SendAsync(HttpMethod.Post, ImportPath, body: stranger);
        await ApiAssert.ProblemAsync(unexpected, HttpStatusCode.UnprocessableEntity, "validation_failed");

        using HttpResponseMessage notAZip = await host.SendAsync(HttpMethod.Post, ImportPath, body: Encoding.UTF8.GetBytes("this is not a zip file"));
        await ApiAssert.ProblemAsync(notAZip, HttpStatusCode.UnprocessableEntity, "validation_failed");

        using HttpResponseMessage empty = await host.SendAsync(HttpMethod.Post, ImportPath, body: []);
        await ApiAssert.ProblemAsync(empty, HttpStatusCode.BadRequest, "bad_request");

        await AssertNothingAppliedAsync(host);
    }

    [Fact]
    public async Task The_diagnostics_bundle_contains_no_token_password_hash_or_key()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command("say_hello"));

        // A paired client gives a link token (the agent keeps only its hash).
        using HttpResponseMessage started = await host.PostAsync("/api/v1/link/pairing");
        string code = (await ApiAssert.JsonAsync(started))["code"]!.GetValue<string>();
        PairResult paired = await host.Service<PairingService>().PairAsync(code, new PairClientInfo("tool", "Archive test"), CancellationToken.None);
        Assert.True(paired.Succeeded);
        string linkToken = paired.Token!;

        // A component that logs a secret by mistake: the bundle still must not carry it.
        string logs = host.Service<IAppPaths>().LogsDirectory;
        Directory.CreateDirectory(logs);
        string passwordHash = (await host.Service<ISecretStore>().GetStringAsync(SecretNames.PasswordHash, CancellationToken.None))
            ?? throw new InvalidOperationException("The agent stores no password hash after the setup.");
        string leaky = $"{{\"message\":\"authorization failed for token {linkToken} hash {passwordHash}\"}}\nBearer {linkToken}\nplain line\n";
        await File.WriteAllTextAsync(Path.Combine(logs, "agent-20990101.log"), leaky);

        using HttpResponseMessage response = await host.GetAsync("/api/v1/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        byte[] bundle = await response.Content.ReadAsByteArrayAsync();
        Dictionary<string, byte[]> entries = ReadEntries(bundle);

        Assert.Contains("diagnostics.json", entries.Keys);
        Assert.Contains("settings.json", entries.Keys);
        Assert.Contains("logs/agent.log", entries.Keys);
        Assert.Contains("plain line", Encoding.UTF8.GetString(entries["logs/agent.log"]), StringComparison.Ordinal);
        Assert.DoesNotContain(entries.Keys, name => name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        await AssertNoSecretsAsync(host, entries, "the diagnostics bundle", linkToken, passwordHash);
    }

    private static async Task CreateSensorAsync(ApiTestHost host, string id, string process)
    {
        using HttpResponseMessage response = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor(id, process));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<byte[]> ExportAsync(ApiTestHost host)
    {
        using HttpResponseMessage response = await host.GetAsync(ExportPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<JsonArray> ListAsync(ApiTestHost host, string path, string member)
    {
        using HttpResponseMessage response = await host.GetAsync(path);
        JsonNode body = await ApiAssert.JsonAsync(response);
        return body[member]!.AsArray();
    }

    private static async Task<string[]> IdsAsync(ApiTestHost host, string path, string member, string key)
    {
        JsonArray items = await ListAsync(host, path, member);
        return [.. items.Select(item => item![key]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    private static async Task AssertNothingAppliedAsync(ApiTestHost host)
    {
        Assert.Empty(await ListAsync(host, "/api/v1/commands", "commands"));
        Assert.Empty(await ListAsync(host, "/api/v1/sensors", "sensors"));
        Assert.Empty(await ListAsync(host, "/api/v1/scripts", "scripts"));
        string scripts = Path.Combine(host.DataPath, "scripts");
        Assert.True(!Directory.Exists(scripts) || Directory.GetFiles(scripts).Length == 0, "A script was written by a rejected import.");
    }

    private static byte[] BuildArchive(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using Stream target = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                target.Write(bytes, 0, bytes.Length);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Every entry of the archive with its unpacked content.</summary>
    private static Dictionary<string, byte[]> ReadEntries(byte[] archive)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            using Stream source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            result[entry.FullName] = buffer.ToArray();
        }

        return result;
    }

    /// <summary>
    /// Scans every entry (names and content) for everything that must stay on the PC: the password, the password hash, the
    /// setup token, link tokens, the contents of the secrets directory (the AES key, the encrypted secrets, the
    /// certificate with its private key), in plain form, base64 and hex, and for PEM private keys.
    /// </summary>
    private static async Task AssertNoSecretsAsync(ApiTestHost host, Dictionary<string, byte[]> entries, string what, params string[] knownSecrets)
    {
        var needles = new List<(string Label, byte[] Bytes)>();
        foreach (string secret in knownSecrets)
        {
            AddNeedle(needles, "a known secret", Encoding.UTF8.GetBytes(secret));
        }

        AddNeedle(needles, "the password", Encoding.UTF8.GetBytes(TestAgent.StrongPassword));
        ISecretStore store = host.Service<ISecretStore>();
        foreach (string name in await store.ListNamesAsync(CancellationToken.None))
        {
            if (await store.GetBytesAsync(name, CancellationToken.None) is { } value)
            {
                AddNeedle(needles, "the secret " + name, value);
            }
        }

        string secrets = host.Agent.SecretsDirectory;
        if (Directory.Exists(secrets))
        {
            foreach (string file in Directory.GetFiles(secrets))
            {
                byte[] content = await File.ReadAllBytesAsync(file);
                string label = "the file " + Path.GetFileName(file);
                AddNeedle(needles, label, content);

                // Text files (the setup token, the JSON of the secrets) are also checked line by line and value by value.
                foreach (string line in Encoding.UTF8.GetString(content).Split('\n', '\r', '"'))
                {
                    if (line.Trim().Length >= 24)
                    {
                        AddNeedle(needles, label, Encoding.UTF8.GetBytes(line.Trim()));
                    }
                }
            }
        }

        Assert.NotEmpty(needles);
        foreach ((string entryName, byte[] content) in entries)
        {
            Assert.DoesNotContain("secrets/", entryName, StringComparison.OrdinalIgnoreCase);
            foreach ((string label, byte[] needle) in needles)
            {
                Assert.False(content.AsSpan().IndexOf(needle) >= 0, $"{what}: the entry {entryName} contains {label}.");
            }

            string text = Encoding.UTF8.GetString(content);
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("pbkdf2-sha256$", text, StringComparison.Ordinal);
        }
    }

    /// <summary>Adds the value, its base64 and its hex form (and the url-safe base64) when it is long enough to be unique.</summary>
    private static void AddNeedle(List<(string Label, byte[] Bytes)> needles, string label, byte[] value)
    {
        if (value.Length < 12)
        {
            return;
        }

        needles.Add((label, value));
        string base64 = Convert.ToBase64String(value);
        needles.Add((label + " (base64)", Encoding.ASCII.GetBytes(base64)));
        needles.Add((label + " (url-safe base64)", Encoding.ASCII.GetBytes(base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'))));
        needles.Add((label + " (hex)", Encoding.ASCII.GetBytes(Convert.ToHexString(value))));
        needles.Add((label + " (lower hex)", Encoding.ASCII.GetBytes(Convert.ToHexStringLower(value))));
    }
}
