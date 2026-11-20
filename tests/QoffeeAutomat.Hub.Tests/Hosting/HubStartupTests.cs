using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Security;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Tests.Hosting;

/// <summary>
/// The whole hub on a real Kestrel listener with a temporary data directory (T902): an empty data directory, the identity and the keys that
/// stay the same across restarts, the three ways of terminating TLS, <c>/healthz</c> and graceful shutdown.
/// </summary>
[Trait("Category", "Hosting")]
public sealed class HubStartupTests
{
    private static HubOptions Options(string dataDirectory, HubTlsMode tls = HubTlsMode.SelfSigned, IReadOnlyList<string>? proxies = null, string? name = null)
    {
        return new HubOptions
        {
            DataDir = dataDirectory,
            Port = FreePort(),
            Bind = "127.0.0.1",
            Tls = tls,
            TrustedProxies = proxies ?? [],
            Name = name,
            LogFormat = HubLogFormat.Text,
        };
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A client that accepts exactly the certificate with this fingerprint (what an agent does after enrollment).</summary>
    private static HttpClient PinnedClient(string? fingerprint)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null
            && fingerprint is not null
            && CertificateFingerprint.AreEqual(CertificateFingerprint.Format(certificate.GetCertHash(HashAlgorithmName.SHA256)), fingerprint);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    [UnsupportedOSPlatform("windows")]
    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path);

    [Fact]
    public async Task An_empty_data_directory_is_enough_to_start_and_serve_healthz_over_https()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("hub-data");
        HubOptions options = Options(root);

        await using RunningHub hub = await RunningHub.StartAsync(options);

        Assert.True(File.Exists(Path.Combine(root, "hub.db")));
        Assert.True(File.Exists(Path.Combine(root, "secrets", "secrets.key")));
        Assert.True(File.Exists(Path.Combine(root, "secrets", "secrets.json")));
        string fingerprint = hub.Service<HubCertificateProvider>().Fingerprint ?? throw new InvalidOperationException("The hub has no certificate.");
        using HttpClient client = PinnedClient(fingerprint);
        using HttpResponseMessage response = await client.GetAsync($"https://127.0.0.1:{options.Port}/healthz");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_certificate_is_self_signed_ecdsa_p256_and_the_signing_key_is_a_p256_key()
    {
        using var directory = new TempDirectory();

        await using RunningHub hub = await RunningHub.StartAsync(Options(directory.Below("data")));

        X509Certificate2 certificate = hub.Service<HubCertificateProvider>().TryGetCertificate() ?? throw new InvalidOperationException("No certificate.");
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.Equal(256, certificate.GetECDsaPublicKey()?.KeySize);
        Assert.Equal(HubTlsMode.SelfSigned, hub.Service<HubCertificateProvider>().Mode);
        Assert.True(HubSigningKey.TryImportPublicKey(hub.Service<HubSigningKeyStore>().PublicKey, out ECDsa? signingKey));
        signingKey?.Dispose();
    }

    [Fact]
    public async Task Identity_certificate_and_signing_key_stay_the_same_across_restarts()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("data");

        string firstId;
        string firstName;
        string? firstFingerprint;
        string firstPublicKey;
        await using (RunningHub first = await RunningHub.StartAsync(Options(root, name: "Cellar hub")))
        {
            firstId = first.Service<HubIdentity>().Id;
            firstName = first.Service<HubIdentity>().Name;
            firstFingerprint = first.Service<HubCertificateProvider>().Fingerprint;
            firstPublicKey = first.Service<HubSigningKeyStore>().PublicKey;
            Assert.True(first.Service<HubCertificateProvider>().CreatedNewCertificate);
            Assert.True(first.Service<HubSigningKeyStore>().CreatedNewKey);
        }

        // The name option only names a new hub; an existing hub keeps its name and its id.
        await using RunningHub second = await RunningHub.StartAsync(Options(root, name: "Another name"));

        Assert.Matches("^[0-9a-f]{32}$", firstId);
        Assert.Equal("Cellar hub", firstName);
        Assert.Equal(firstId, second.Service<HubIdentity>().Id);
        Assert.Equal(firstName, second.Service<HubIdentity>().Name);
        Assert.NotNull(firstFingerprint);
        Assert.Equal(firstFingerprint, second.Service<HubCertificateProvider>().Fingerprint);
        Assert.Equal(firstPublicKey, second.Service<HubSigningKeyStore>().PublicKey);
        Assert.False(second.Service<HubCertificateProvider>().CreatedNewCertificate);
        Assert.False(second.Service<HubSigningKeyStore>().CreatedNewKey);
        Assert.Equal(HubSchema.LatestVersion, second.Service<HubDatabase>().InitialSchemaVersion);
    }

    [Fact]
    public async Task A_restarted_hub_still_answers_to_a_client_that_pinned_the_old_fingerprint()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("data");

        string pinned;
        await using (RunningHub first = await RunningHub.StartAsync(Options(root)))
        {
            pinned = first.Service<HubCertificateProvider>().Fingerprint!;
        }

        HubOptions options = Options(root);
        await using RunningHub second = await RunningHub.StartAsync(options);
        using HttpClient client = PinnedClient(pinned);
        using HttpResponseMessage response = await client.GetAsync($"https://127.0.0.1:{options.Port}/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_secrets_are_not_stored_as_plain_text()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("data");

        string publicKey;
        await using (RunningHub hub = await RunningHub.StartAsync(Options(root)))
        {
            publicKey = hub.Service<HubSigningKeyStore>().PublicKey;
        }

        string secretsFile = await File.ReadAllTextAsync(Path.Combine(root, "secrets", "secrets.json"));

        // The names are visible, the values (the PFX with the TLS key and the PKCS#8 signing key) are encrypted with the key file.
        Assert.Contains(SecretNames.TlsCertificate, secretsFile, StringComparison.Ordinal);
        Assert.Contains(HubSecretNames.SigningKey, secretsFile, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", secretsFile, StringComparison.Ordinal);
        Assert.DoesNotContain(publicKey, secretsFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directories_get_mode_0700_and_secret_files_mode_0600_on_linux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        string root = directory.Below("data");

        await using (await RunningHub.StartAsync(Options(root)))
        {
            const UnixFileMode directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            Assert.Equal(directoryMode, ModeOf(root));
            Assert.Equal(directoryMode, ModeOf(Path.Combine(root, "secrets")));
            Assert.Equal(fileMode, ModeOf(Path.Combine(root, "hub.db")));
            Assert.Equal(fileMode, ModeOf(Path.Combine(root, "secrets", "secrets.key")));
            Assert.Equal(fileMode, ModeOf(Path.Combine(root, "secrets", "secrets.json")));
        }
    }

    [Fact]
    public async Task An_existing_data_directory_that_others_can_enter_is_restricted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        string root = directory.Below("data");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        await using (await RunningHub.StartAsync(Options(root)))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ModeOf(root));
        }
    }

    [Fact]
    public async Task Plain_http_behind_a_trusted_proxy_has_no_certificate()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("data");
        HubOptions options = Options(root, HubTlsMode.None, ["127.0.0.1"]);

        await using RunningHub hub = await RunningHub.StartAsync(options);

        using var client = new HttpClient();
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{options.Port}/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(hub.Service<HubCertificateProvider>().Fingerprint);
        Assert.DoesNotContain(SecretNames.TlsCertificate, await hub.Service<ISecretStore>().ListNamesAsync(CancellationToken.None));
        Assert.Contains(hub.Logs.Messages, message => message.Contains("Forwarded headers are honoured for 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_certificate_of_the_operator_is_used_instead_of_a_self_signed_one()
    {
        using var directory = new TempDirectory();
        string pfxPath = directory.Below("hub.pfx");
        await File.WriteAllBytesAsync(pfxPath, SelfSignedCertificateFactory.CreatePfx("hub.example.test", DateTimeOffset.UtcNow, TimeSpan.FromDays(30)));
        string root = directory.Below("data");
        HubOptions options = Options(root) with { Tls = HubTlsMode.Certificate, TlsCertificate = pfxPath };

        await using RunningHub hub = await RunningHub.StartAsync(options);

        string fingerprint = hub.Service<HubCertificateProvider>().Fingerprint!;
        using X509Certificate2 expected = SelfSignedCertificateFactory.Load(await File.ReadAllBytesAsync(pfxPath));
        Assert.Equal(CertificateFingerprint.Compute(expected), fingerprint);
        using HttpClient client = PinnedClient(fingerprint);
        using HttpResponseMessage response = await client.GetAsync($"https://127.0.0.1:{options.Port}/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(SecretNames.TlsCertificate, await hub.Service<ISecretStore>().ListNamesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_client_that_pins_another_certificate_is_refused()
    {
        using var directory = new TempDirectory();
        HubOptions options = Options(directory.Below("data"));

        await using RunningHub hub = await RunningHub.StartAsync(options);

        using HttpClient client = PinnedClient(new string('A', 2));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{options.Port}/healthz"));
    }

    [Fact]
    public async Task A_damaged_signing_key_stops_the_start_and_is_not_replaced()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("data");
        await using (await RunningHub.StartAsync(Options(root)))
        {
        }

        HubPaths paths = HubPaths.ForRoot(root);
        using (var protector = new FileKeyProtector(paths.SecretKeyPath))
        using (var secrets = new SecretStore(paths.SecretsFilePath, protector))
        {
            await secrets.SetBytesAsync(HubSecretNames.SigningKey, new byte[] { 1, 2, 3 }, CancellationToken.None);
        }

        WebApplication app = HubHost.Build(Options(root));
        try
        {
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

            Assert.Contains("signing key", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.DisposeAsync();
        }

        using var protector2 = new FileKeyProtector(paths.SecretKeyPath);
        using var secrets2 = new SecretStore(paths.SecretsFilePath, protector2);
        Assert.Equal(new byte[] { 1, 2, 3 }, await secrets2.GetBytesAsync(HubSecretNames.SigningKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_database_of_a_newer_hub_stops_the_start()
    {
        using var directory = new TempDirectory();
        string root = directory.Below("data");
        await using (await RunningHub.StartAsync(Options(root)))
        {
        }

        using (var database = HubDatabase.Open(HubPaths.ForRoot(root).DatabasePath))
        {
            await database.ExecuteAsync(
                connection => HubSql.Execute(connection, null, "PRAGMA user_version = 999;"),
                CancellationToken.None);
        }

        WebApplication app = HubHost.Build(Options(root));
        try
        {
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

            Assert.Contains("newer hub", failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_hub_logs_who_it_is_and_shuts_down_gracefully()
    {
        using var directory = new TempDirectory();
        HubOptions options = Options(directory.Below("data"));

        RunningHub hub = await RunningHub.StartAsync(options);
        string id = hub.Service<HubIdentity>().Id;
        string fingerprint = hub.Service<HubCertificateProvider>().Fingerprint!;
        using (HttpClient client = PinnedClient(fingerprint))
        {
            using HttpResponseMessage response = await client.GetAsync($"https://127.0.0.1:{options.Port}/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Task stopping = hub.StopAsync();
        Task finished = await Task.WhenAny(stopping, Task.Delay(HubHost.ShutdownTimeout + TimeSpan.FromSeconds(5)));
        await hub.DisposeAsync();

        Assert.Same(stopping, finished);
        Assert.Contains(hub.Logs.Messages, message => message.Contains(id, StringComparison.Ordinal) && message.Contains("started", StringComparison.Ordinal));
        Assert.Contains(hub.Logs.Messages, message => message.Contains(fingerprint, StringComparison.Ordinal));
        Assert.Contains(hub.Logs.Messages, message => message.Contains("The hub is stopping.", StringComparison.Ordinal));
        using var client2 = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client2.GetAsync($"https://127.0.0.1:{options.Port}/healthz"));
    }

    /// <summary>A started hub with a real listener.</summary>
    private sealed class RunningHub : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private bool _stopped;

        private RunningHub(WebApplication app, LogCapture logs)
        {
            _app = app;
            Logs = logs;
        }

        public LogCapture Logs { get; }

        public static async Task<RunningHub> StartAsync(HubOptions options)
        {
            var logs = new LogCapture();
            WebApplication app = HubHost.Build(options, services => services.AddSingleton<ILoggerProvider>(logs));
            try
            {
                await app.StartAsync();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            return new RunningHub(app, logs);
        }

        public T Service<T>()
            where T : notnull
        {
            return _app.Services.GetRequiredService<T>();
        }

        public Task StopAsync()
        {
            _stopped = true;
            return _app.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                await StopAsync();
            }

            await _app.DisposeAsync();
        }
    }
}
