using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Core.Execution.PowerShell;

/// <summary>
/// The wrapper script and the engine command line of PLAN.md D-012. Variable values are never spliced into
/// script text or into the command line: the engine runs <c>-File &lt;wrapper&gt;</c>, the wrapper reads the JSON
/// parameter file that <c>QA_PARAMS_FILE</c> names and passes the values to the target script (named by
/// <c>QA_TARGET_SCRIPT</c>) by splatting a hashtable, so they arrive as typed named parameters.
/// </summary>
public static class PowerShellWrapper
{
    /// <summary>The environment variable that names the JSON parameter file.</summary>
    public const string ParametersFileVariable = "QA_PARAMS_FILE";

    /// <summary>The environment variable that names the script the wrapper invokes.</summary>
    public const string TargetScriptVariable = "QA_TARGET_SCRIPT";

    /// <summary>
    /// The wrapper. It uses only syntax and .NET members that exist in Windows PowerShell 5.1 and in
    /// PowerShell 7, contains only ASCII characters and no variable data. Its exit code is the one the target
    /// script reports with <c>exit</c> (or the exit code of the last native command it ran); a terminating
    /// error becomes exit code 1 with the message on standard error.
    /// </summary>
    public const string Script = """
        # QoffeeAutomat PowerShell wrapper (PLAN.md D-012). Generated for every run; do not edit.
        # Values are read from the JSON file named by QA_PARAMS_FILE and bound to the target script
        # (QA_TARGET_SCRIPT) by splatting, so they are typed named parameters and never script text.
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $qaExitCode = 0
        try {
            try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

            $qaParametersFile = $env:QA_PARAMS_FILE
            $qaTarget = $env:QA_TARGET_SCRIPT
            if ([string]::IsNullOrEmpty($qaParametersFile)) { throw 'QA_PARAMS_FILE is not set.' }
            if ([string]::IsNullOrEmpty($qaTarget)) { throw 'QA_TARGET_SCRIPT is not set.' }

            $qaDocument = [System.IO.File]::ReadAllText($qaParametersFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            $qaArguments = @{}
            if ($null -ne $qaDocument.parameters) {
                foreach ($qaEntry in $qaDocument.parameters.PSObject.Properties) {
                    $qaName = [string]$qaEntry.Name
                    $qaType = [string]$qaEntry.Value.type
                    $qaText = [string]$qaEntry.Value.value
                    if ($qaType -eq 'string') {
                        $qaArguments[$qaName] = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($qaText))
                    } elseif ($qaType -eq 'int') {
                        $qaNumber = [long]::Parse($qaText, [System.Globalization.CultureInfo]::InvariantCulture)
                        if ($qaNumber -ge [int]::MinValue -and $qaNumber -le [int]::MaxValue) {
                            $qaArguments[$qaName] = [int]$qaNumber
                        } else {
                            $qaArguments[$qaName] = $qaNumber
                        }
                    } elseif ($qaType -eq 'float') {
                        $qaArguments[$qaName] = [double]::Parse($qaText, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture)
                    } elseif ($qaType -eq 'bool') {
                        $qaArguments[$qaName] = [bool]::Parse($qaText)
                    } else {
                        throw ('Unknown parameter type for ' + $qaName + '.')
                    }
                }
            }

            $ErrorActionPreference = 'Continue'
            $global:LASTEXITCODE = 0
            & $qaTarget @qaArguments
            if ($null -ne $global:LASTEXITCODE) { $qaExitCode = [int]$global:LASTEXITCODE }
        } catch {
            $qaMessage = $null
            try { $qaMessage = $_.Exception.Message } catch { }
            if ([string]::IsNullOrEmpty($qaMessage)) { $qaMessage = [string]$_ }
            [Console]::Error.WriteLine($qaMessage)
            if ($null -ne $_.InvocationInfo -and -not [string]::IsNullOrEmpty($_.InvocationInfo.PositionMessage)) {
                [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage)
            }

            $qaExitCode = 1
        }

        exit $qaExitCode

        """;

    /// <summary>The arguments that start an engine on the wrapper: <c>-NoProfile -NonInteractive -ExecutionPolicy Bypass -File &lt;wrapper&gt;</c>.</summary>
    /// <param name="wrapperPath">The full path of the wrapper file.</param>
    /// <returns>The argv entries; the wrapper path is one entry whatever characters it contains.</returns>
    public static IReadOnlyList<string> BuildArguments(string wrapperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wrapperPath);
        return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapperPath];
    }
}

/// <summary>
/// The JSON parameter file that the wrapper reads (PLAN.md D-012). The format is
/// <c>{"version":1,"parameters":{"Name":{"type":"string|int|float|bool","value":"..."}}}</c>. Every value is a JSON
/// string: text is Base64 of its UTF-8 bytes, numbers and booleans use the invariant culture. That keeps the file
/// pure ASCII and keeps both engines' JSON readers (which turn date-like strings into dates and numbers into
/// engine-specific types) from altering a value; the wrapper restores the exact .NET type.
/// </summary>
public static class PowerShellParameterFile
{
    /// <summary>The value of the <c>version</c> member.</summary>
    public const int Version = 1;

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,31}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Serialises validated variables (<see cref="string"/>, integers, <see cref="double"/>, <see cref="bool"/>).</summary>
    /// <param name="variables">The variables by name. Names must match the parameter name rule of PLAN.md S06.</param>
    /// <returns>The file content: ASCII, members in ordinal order, so equal variables give equal bytes.</returns>
    /// <exception cref="ArgumentException">A name is not a valid parameter name or a value has an unsupported type.</exception>
    public static byte[] Serialize(IReadOnlyDictionary<string, object> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartObject("parameters");
            foreach ((string name, object value) in variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!NamePattern.IsMatch(name))
                {
                    throw new ArgumentException($"'{name}' is not a valid parameter name.", nameof(variables));
                }

                (string type, string text) = Encode(name, value);
                writer.WriteStartObject(name);
                writer.WriteString("type", type);
                writer.WriteString("value", text);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static (string Type, string Text) Encode(string name, object value)
    {
        return value switch
        {
            string text => ("string", Convert.ToBase64String(Encoding.UTF8.GetBytes(text))),
            bool flag => ("bool", flag ? "true" : "false"),
            double number => ("float", number.ToString("R", CultureInfo.InvariantCulture)),
            float number => ("float", ((double)number).ToString("R", CultureInfo.InvariantCulture)),
            long or int or short or byte or sbyte or ushort or uint => ("int", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException($"The value of '{name}' has the unsupported type {value.GetType().Name}.", nameof(value)),
        };
    }
}
