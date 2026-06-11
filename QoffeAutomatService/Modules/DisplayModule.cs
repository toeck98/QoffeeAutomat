using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace QoffeAutomatService.Services
{
    public class DisplayModule : IServiceModule
    {
        private readonly ILogger<DisplayModule> _logger;
        private const string ModesPathDefault = @"C:\ProgramData\Automation\Modes";

        public DisplayModule(ILogger<DisplayModule> logger)
        {
            _logger = logger;
        }

        public async Task HandleAsync(string entity, string state, CancellationToken ct)
        {
            _logger.LogInformation("DisplayModule handling {Entity} -> {State}", entity, state);

            if (string.IsNullOrWhiteSpace(state))
            {
                _logger.LogWarning("No display configuration name provided in state for entity {Entity}.", entity);
                return;
            }

            string configName = state;
            bool updateAdapterIds = false;
            var split = state.Split(new[] { '|' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length > 0) configName = split[0].Trim();
            if (split.Length > 1)
            {
                var flags = split[1].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var f in flags)
                {
                    if (string.Equals(f.Trim(), "updateadapterids", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Trim(), "update", StringComparison.OrdinalIgnoreCase))
                    {
                        updateAdapterIds = true;
                    }
                }
            }

            try
            {
                var resolvedPath = ResolveConfigPath(configName, ModesPathDefault);

                await Task.Run(() =>
                {
                    using var runspace = RunspaceFactory.CreateRunspace();
                    runspace.Open();

                    using var ps = PowerShell.Create();
                    ps.Runspace = runspace;

                    // Try to import DisplayConfig module; ignore errors (module might already be loaded)
                    try
                    {
                        ps.AddCommand("Import-Module").AddArgument("DisplayConfig");
                        ps.Invoke();
                        ps.Commands.Clear();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Import-Module DisplayConfig failed or not available: {Message}", ex.Message);
                        ps.Commands.Clear();
                    }

                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        var escaped = resolvedPath.Replace("'", "''");
                        var script = $"Import-Clixml -Path '{escaped}' | Use-DisplayConfig{(updateAdapterIds ? " -UpdateAdapterIds" : string.Empty)}";
                        ps.AddScript(script);

                        var result = ps.Invoke();
                        if (ps.HadErrors)
                        {
                            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                            _logger.LogError("Applying display config from path failed: {Errors}", errors);
                        }
                        else
                        {
                            _logger.LogInformation("Applied display config from {Path}", resolvedPath);
                        }

                        ps.Commands.Clear();
                        return;
                    }

                    // Fall back: try to call Set-DisplayConfig function (script-based) if available in the runspace
                    try
                    {
                        ps.AddCommand("Set-DisplayConfig").AddParameter("ConfigName", configName);
                        if (updateAdapterIds) ps.AddParameter("UpdateAdapterIds", true);

                        var outp = ps.Invoke();
                        if (ps.HadErrors)
                        {
                            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                            _logger.LogError("Set-DisplayConfig failed: {Errors}", errors);
                        }
                        else
                        {
                            _logger.LogInformation("Set-DisplayConfig applied for {Config}", configName);
                        }
                    }
                    catch (CommandNotFoundException)
                    {
                        _logger.LogError("Could not resolve display configuration '{Config}' and Set-DisplayConfig is not available in the runspace.", configName);
                    }
                    finally
                    {
                        ps.Commands.Clear();
                    }
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Display configuration task was canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while applying display configuration {Config}.", state);
            }
        }

        private static string? ResolveConfigPath(string configName, string modesPath)
        {
            if (File.Exists(configName)) return Path.GetFullPath(configName);

            if (!Directory.Exists(modesPath)) return null;

            // try exact file name variations
            var candidates = new[]
            {
                configName,
                configName + ".xml",
                configName.Replace(' ', '-') + ".xml",
                configName.Replace('-', ' ') + ".xml"
            };

            foreach (var c in candidates)
            {
                var full = Path.Combine(modesPath, c);
                if (File.Exists(full)) return full;
            }

            // fallback: search for any file that contains the configName (case-insensitive)
            var files = Directory.EnumerateFiles(modesPath, "*.xml");
            var match = files.FirstOrDefault(f => Path.GetFileName(f).IndexOf(configName, StringComparison.OrdinalIgnoreCase) >= 0);
            return match;
        }
    }
}
