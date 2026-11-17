using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Execution;

namespace QoffeeAutomat.Agent.Tests.Api.Runtime;

/// <summary>The named secrets of scripts through the management API: write-only values and no leak anywhere (T706).</summary>
[Trait("Category", "NamedSecrets")]
public sealed class NamedSecretApiTests
{
    private const string Secret = "Quartz-Zebra-7731-Hunter";
    private const string SecretsPath = "/api/v1/secrets";
    private const string CommandJson =
        """{"id":"use_secret","name":"Use secret","enabled":true,"exposeToHa":false,"runAs":"system","action":{"type":"process","file":"secret-tool","args":["go"]},"captureOutput":true,"secrets":["api_token"]}""";

    private static string ValueBody(string value) => "{\"value\":\"" + value + "\"}";

    private static async Task<string> PutSecretAsync(ApiTestHost host, string name, string value = Secret)
    {
        using HttpResponseMessage response = await host.PutAsync(SecretsPath + "/" + name, ValueBody(value));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return body;
    }

    [Fact]
    public async Task A_secret_is_created_replaced_listed_and_deleted_and_its_value_is_never_returned()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage created = await host.PutAsync(SecretsPath + "/api_token", ValueBody(Secret));
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/secrets/api_token", created.Headers.Location?.OriginalString);
        Assert.DoesNotContain(Secret, createdBody, StringComparison.Ordinal);
        Assert.Equal("api_token", JsonNode.Parse(createdBody)!["name"]!.GetValue<string>());

        using HttpResponseMessage replaced = await host.PutAsync(SecretsPath + "/api_token", ValueBody("Another-Value-2468"));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        await host.CreateCommandAsync(CommandJson);
        using HttpResponseMessage listed = await host.GetAsync(SecretsPath);
        string listBody = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, listBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Another-Value-2468", listBody, StringComparison.Ordinal);
        JsonNode entry = Assert.Single(JsonNode.Parse(listBody)!["secrets"]!.AsArray())!;
        Assert.Equal("api_token", entry["name"]!.GetValue<string>());
        Assert.Equal("use_secret", Assert.Single(entry["usedBy"]!.AsArray())!.GetValue<string>());

        using HttpResponseMessage deleted = await host.DeleteAsync(SecretsPath + "/api_token");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage again = await host.DeleteAsync(SecretsPath + "/api_token");
        await ApiAssert.ProblemAsync(again, HttpStatusCode.NotFound, "unknown_secret");
        using HttpResponseMessage empty = await host.GetAsync(SecretsPath);
        Assert.Empty((await ApiAssert.JsonAsync(empty))["secrets"]!.AsArray());
    }

    [Fact]
    public async Task An_invalid_name_or_value_is_rejected_with_the_path_of_the_problem()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage badName = await host.PutAsync(SecretsPath + "/Bad-Name", ValueBody(Secret));
        JsonNode nameProblem = await ApiAssert.ProblemAsync(badName, (HttpStatusCode)422, "validation_failed");
        string[] namePaths = ["/name"];
        Assert.Equal(namePaths, ApiAssert.ErrorPaths(nameProblem));

        using HttpResponseMessage shortValue = await host.PutAsync(SecretsPath + "/api_token", ValueBody("abc"));
        JsonNode valueProblem = await ApiAssert.ProblemAsync(shortValue, (HttpStatusCode)422, "validation_failed");
        string[] valuePaths = ["/value"];
        Assert.Equal(valuePaths, ApiAssert.ErrorPaths(valueProblem));
        Assert.DoesNotContain("abc\"", await shortValue.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage list = await host.GetAsync(SecretsPath);
        Assert.Empty((await ApiAssert.JsonAsync(list))["secrets"]!.AsArray());
    }

    [Fact]
    public async Task A_secret_that_a_script_prints_is_masked_everywhere_and_the_archives_hold_no_secret_value()
    {
        var runner = new ControlledProcessRunner();
        await using ApiTestHost host = await RuntimeHost.StartAsync(runner);
        await PutSecretAsync(host, "api_token");
        await host.CreateCommandAsync(CommandJson);

        using HttpResponseMessage started = await host.PostAsync("/api/v1/commands/use_secret/run", "{}");
        string runId = (await ApiAssert.JsonAsync(started, HttpStatusCode.Accepted))["runId"]!.GetValue<string>();
        ControlledProcessRun process = await runner.WaitForRunAsync();
        Assert.Equal(Secret, process.Request.EnvironmentVariables["QA_SECRET_API_TOKEN"]);
        Assert.DoesNotContain(process.Request.Arguments, argument => argument.Contains(Secret, StringComparison.Ordinal));

        await using ServerEventReader stream = await ServerEventReader.OpenAsync(host, "/api/v1/runs/" + runId + "/stream");
        process.Finish(0, $"token is {Secret}\n", $"warning {Secret}\n");

        var streamed = new StringBuilder();
        while (true)
        {
            ServerEvent next = await stream.ReadAsync();
            if (next.Name == "end")
            {
                break;
            }

            streamed.Append(next.Data["text"]!.GetValue<string>());
        }

        Assert.DoesNotContain(Secret, streamed.ToString(), StringComparison.Ordinal);
        Assert.Contains("token is ***", streamed.ToString(), StringComparison.Ordinal);

        await WaitUntilFinishedAsync(host, runId);
        using HttpResponseMessage detail = await host.GetAsync("/api/v1/runs/" + runId);
        JsonNode run = await ApiAssert.JsonAsync(detail);
        Assert.Equal("token is ***\n", run["standardOutput"]!.GetValue<string>());
        Assert.Equal("warning ***\n", run["standardError"]!.GetValue<string>());
        using HttpResponseMessage history = await host.GetAsync("/api/v1/runs");
        Assert.DoesNotContain(Secret, await history.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using HttpResponseMessage events = await host.GetAsync("/api/v1/events");
        Assert.DoesNotContain(Secret, await events.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The archive scan: neither the export nor the diagnostics bundle contains the value, in any common encoding.
        using HttpResponseMessage export = await host.GetAsync("/api/v1/config/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Dictionary<string, byte[]> exported = ReadEntries(await export.Content.ReadAsByteArrayAsync());
        Assert.Contains("config/commands/use_secret.json", exported.Keys);
        Assert.Contains("api_token", Encoding.UTF8.GetString(exported["config/commands/use_secret.json"]), StringComparison.Ordinal);
        AssertNoSecret(exported, "the export");

        using HttpResponseMessage diagnostics = await host.GetAsync("/api/v1/diagnostics");
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        AssertNoSecret(ReadEntries(await diagnostics.Content.ReadAsByteArrayAsync()), "the diagnostics bundle");
    }

    private static void AssertNoSecret(Dictionary<string, byte[]> entries, string what)
    {
        byte[] plain = Encoding.UTF8.GetBytes(Secret);
        (string Label, byte[] Bytes)[] needles =
        [
            ("the value", plain),
            ("its base64 form", Encoding.ASCII.GetBytes(Convert.ToBase64String(plain))),
            ("its hex form", Encoding.ASCII.GetBytes(Convert.ToHexString(plain))),
            ("its lower-case hex form", Encoding.ASCII.GetBytes(Convert.ToHexStringLower(plain))),
            ("its SHA-256 hash", Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(plain)))),
        ];

        Assert.NotEmpty(entries);
        foreach ((string entryName, byte[] content) in entries)
        {
            foreach ((string label, byte[] needle) in needles)
            {
                Assert.False(content.AsSpan().IndexOf(needle) >= 0, $"{what}: the entry {entryName} contains {label} of a secret.");
            }
        }
    }

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

    private static async Task WaitUntilFinishedAsync(ApiTestHost host, string runId)
    {
        DateTime deadline = DateTime.UtcNow + TestWait.DefaultTimeout;
        while (true)
        {
            using HttpResponseMessage response = await host.GetAsync("/api/v1/runs/" + runId);
            JsonNode run = await ApiAssert.JsonAsync(response);
            if (run["state"]!.GetValue<string>() == "finished")
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "The run " + runId + " did not finish.");
            await Task.Delay(20);
        }
    }
}
