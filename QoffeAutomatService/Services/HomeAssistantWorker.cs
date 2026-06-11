using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QoffeAutomatService.Services
{
    public class HomeAssistantWorker : BackgroundService
    {
        private readonly ILogger<HomeAssistantWorker> _logger;
        private readonly HomeAssistantOptions _options;
        private readonly HomeAssistantClient _client;
        private readonly IServiceModule _displayModule;
        private readonly Dictionary<string, ModeEntry> _modes = new(StringComparer.OrdinalIgnoreCase);

        public HomeAssistantWorker(ILogger<HomeAssistantWorker> logger, IOptions<HomeAssistantOptions> options, HomeAssistantClient client, IServiceModule displayModule)
        {
            _logger = logger;
            _options = options.Value;
            _client = client;
            _displayModule = displayModule;

            LoadModesConfig();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HomeAssistantWorker starting. Url={Url}, TokenSet={HasToken}", _options.Url, !string.IsNullOrEmpty(_options.Token));

            var entityToWatch = _options.EntityToWatch ?? "input_select.toeckpc_mode";
            if (string.IsNullOrWhiteSpace(entityToWatch))
            {
                _logger.LogError("EntityToWatch is not configured. Stopping worker.");
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var state = await _client.GetEntityStateAsync(entityToWatch!, stoppingToken);
                if (state != null)
                {
                    var value = state.State ?? string.Empty;
                    _logger.LogInformation("Received state for {Entity}: {State}", entityToWatch, value);

                    if (_modes.TryGetValue(value, out var mode))
                    {
                        _logger.LogInformation("Applying mode {Mode}", mode.Name);

                        // Apply display config via module (module expects config name or path)
                        try
                        {
                            var displayState = mode.DisplayConfig ?? mode.Name;
                            await _displayModule.HandleAsync(entityToWatch!, displayState, stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error applying display configuration for mode {Mode}", mode.Name);
                        }

                        // Start associated programs sequentially. Support entries that are either a program or a sleep instruction.
                        foreach (var prog in mode.Programs ?? Enumerable.Empty<ProgramEntry>())
                        {
                            try
                            {
                                // If this entry is a Sleep instruction, wait and continue
                                if (prog.SleepSeconds.HasValue)
                                {
                                    var secs = Math.Max(0, prog.SleepSeconds.Value);
                                    _logger.LogInformation("Sleeping {Seconds}s before next program (mode {Mode})", secs, mode.Name);
                                    await Task.Delay(TimeSpan.FromSeconds(secs), stoppingToken).ConfigureAwait(false);
                                    continue;
                                }

                                // Otherwise treat as a program to start
                                StartProgram(prog);

                                // Backwards-compatible per-program delay (optional)
                                if (prog.DelayAfterSeconds.HasValue && prog.DelayAfterSeconds.Value > 0)
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(prog.DelayAfterSeconds.Value), stoppingToken).ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                _logger.LogInformation("Program start cancelled for mode {Mode}", mode.Name);
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to start program {Command} for mode {Mode}", prog.Command, mode.Name);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Unknown mode received: {State}", value);
                    }
                }
            }
        }

        private void StartProgram(ProgramEntry prog)
        {
            if (string.IsNullOrWhiteSpace(prog.Command))
            {
                _logger.LogWarning("Empty command in program entry");
                return;
            }

            string fileName = prog.Command!;
            string arguments = prog.Arguments ?? string.Empty;

            // If Arguments not provided, try to split command into executable and args
            if (string.IsNullOrEmpty(arguments))
            {
                if (fileName.StartsWith("\""))
                {
                    var endQuote = fileName.IndexOf('"', 1);
                    if (endQuote > 1 && fileName.Length > endQuote + 1)
                    {
                        arguments = fileName[(endQuote + 1)..].Trim();
                        fileName = fileName[1..endQuote];
                    }
                }
                else
                {
                    var idx = fileName.IndexOf(' ');
                    if (idx > 0)
                    {
                        arguments = fileName[(idx + 1)..].Trim();
                        fileName = fileName[..idx];
                    }
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
            };

            Process.Start(psi);
            _logger.LogInformation("Started program: {File} {Args}", fileName, arguments);
        }

        private void LoadModesConfig()
        {
            try
            {
                var cfgPath = Path.Combine(AppContext.BaseDirectory, "config", "modes.json");
                if (!File.Exists(cfgPath))
                {
                    _logger.LogWarning("Modes config not found at {Path}; no modes loaded.", cfgPath);
                    return;
                }

                var json = File.ReadAllText(cfgPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg = JsonSerializer.Deserialize<ModesConfig>(json, options);
                if (cfg?.Modes == null) return;

                foreach (var m in cfg.Modes)
                {
                    if (string.IsNullOrWhiteSpace(m.Name)) continue;
                    _modes[m.Name] = m;
                }

                _logger.LogInformation("Loaded {Count} modes from configuration.", _modes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load modes configuration.");
            }
        }

        private class ModesConfig
        {
            public List<ModeEntry>? Modes { get; set; }
        }

        private class ModeEntry
        {
            public string Name { get; set; } = string.Empty;
            public string? DisplayConfig { get; set; }
            public List<ProgramEntry>? Programs { get; set; }
        }

        private class ProgramEntry
        {
            public string? Command { get; set; }
            public string? Arguments { get; set; }
            // New "Sleep" entry in JSON maps to SleepSeconds. Example: { "Sleep": "10" }
            [System.Text.Json.Serialization.JsonPropertyName("Sleep")]
            public int? SleepSeconds { get; set; }
            // Keep legacy per-program delay for backward compatibility
            public int? DelayAfterSeconds { get; set; }
        }
    }
}
