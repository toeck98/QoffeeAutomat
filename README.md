# QoffeAutomat Service

A Windows-based .NET 10 service that integrates with Home Assistant to automate PC display configurations through state monitoring and PowerShell module execution.

## Overview

QoffeAutomat Service is a background service that listens to Home Assistant entity state changes and automatically executes corresponding display configurations on your Windows PC. It's designed to synchronize your display setup with Home Assistant automation scenarios, allowing seamless control of monitor configurations based on home automation rules.

## Features

- **Home Assistant Integration**: Connects to your Home Assistant instance via REST API
- **Real-time State Monitoring**: Watches specific Home Assistant entities for state changes
- **Display Automation**: Executes display configuration profiles based on entity states
- **PowerShell Integration**: Leverages PowerShell modules for script execution and automation
- **Program Launching**: Automatically start applications with custom command-line parameters based on modes
- **Configuration Management**: Easy INI-based configuration for Home Assistant credentials and settings
- **Logging**: Built-in comprehensive logging for debugging and monitoring
- **Validation**: Configuration validation on startup to ensure correct setup

## Architecture

### Core Components

#### HomeAssistantClient
Handles communication with Home Assistant API:
- REST API calls to Home Assistant
- Bearer token authentication
- State polling and monitoring
- Configurable HTTP timeouts

#### HomeAssistantWorker
The main hosted service that orchestrates operations:
- Monitors Home Assistant entity states
- Loads and manages automation modes from JSON configuration
- Triggers display module handlers on state changes
- Launches programs with configurable command-line parameters based on modes
- Supports sequential program execution with delays between programs
- Manages service lifecycle

#### DisplayModule
Implements the main automation logic:
- Parses display configuration names from entity states
- Loads and executes PowerShell display profiles
- Updates adapter IDs and monitor configurations
- Supports modular display management

#### Services Configuration
- **HomeAssistantOptions**: Configuration container for Home Assistant connection details
- **HomeAssistantOptionsValidator**: Validates configuration on startup
- **HomeAssistantJsonContext**: JSON serialization context for API responses

## Installation & Setup

### Prerequisites

- Windows 10 or later
- .NET 10 Runtime
- Home Assistant instance with API access
- Administrator privileges to run as a service

### Configuration

1. Clone or download the project:
```bash
git clone https://github.com/toeck98/QoffeAutomat.git
cd QoffeAutomat/QoffeAutomatService
```

2. Configure Home Assistant settings in `config/homeassistant.conf`:

```ini
[homeassistant]
Token=YOUR_LONG_LIVED_ACCESS_TOKEN
Url=http://your-home-assistant-ip:8123
EntityToWatch=input_select.display_profile
```

**Configuration Parameters:**
- `Token`: Long-lived access token from Home Assistant (Settings → Developer Tools → Create Token)
- `Url`: URL to your Home Assistant instance (http or https)
- `EntityToWatch`: The Home Assistant entity ID to monitor for state changes

3. Create `config/modes.json` to define automation modes with program launch and display configuration settings

### Building

```bash
dotnet build QoffeAutomatService.csproj
```

### Running

As a Console Application:
```bash
dotnet run
```

As a Windows Service (administrator):
```bash
# Install as service
sc.exe create QoffeAutomatService binPath= "C:\path\to\QoffeAutomatService.exe"

# Start service
net start QoffeAutomatService

# Stop service
net stop QoffeAutomatService

# Remove service
sc.exe delete QoffeAutomatService
```

## Usage

### State Change Handling

When the watched entity's state changes in Home Assistant, the service:

1. Receives the new state value
2. Parses the state as a configuration name or command
3. Resolves the corresponding PowerShell profile or script from the modes directory
4. Executes the profile/script with the specified parameters
5. Applies the configuration changes to the system

### Supported State Formats

```
profilename                    # Simple profile name
profilename|updateadapterids   # Profile with adapter update flag
profilename|update             # Shorthand for adapter update
```

### Use Cases

**Multiple Sequential Programs:**
When a mode is activated, all configured programs launch in order with optional delays:
```
Home Assistant Entity State: "gaming"
  → Apply gaming display config
  → Launch Steam
  → Wait 2 seconds
  → Launch NVIDIA GeForce NOW
  → Launch Stream Overlay
```

**Program Chains with Timing:**
```
Home Assistant Entity State: "morning"
  → Launch Outlook
  → Wait 3 seconds
  → Launch Teams
  → Launch VPN Client
  → Apply morning display setup
```

**Simple Program Launch:**
```
Home Assistant Entity State: "movie"
  → Apply TV display config
  → Launch Kodi media center
```

### Example Home Assistant Integration

```yaml
# In Home Assistant automations
- id: display_movie_mode
  trigger:
	platform: state
	entity_id: input_select.display_mode
	to: 'movie'
  action:
	- service: input_select.select_option
	  target:
		entity_id: input_select.display_profile
	  data:
		option: 'movie|update'
```

## Project Structure

```
QoffeAutomatService/
├── Program.cs                          # Entry point and dependency injection setup
├── QoffeAutomatService.csproj          # Project configuration
├── Services/
│   ├── HomeAssistantClient.cs          # Home Assistant API communication
│   ├── HomeAssistantOptions.cs         # Configuration model
│   ├── HomeAssistantOptionsValidator.cs # Configuration validation
│   ├── HomeAssistantState.cs           # State model for entities
│   ├── HomeAssistantWorker.cs          # Main service worker
│   ├── HomeAssistantJsonContext.cs     # JSON serialization
│   └── IServiceModule.cs               # Service module interface
├── Modules/
│   └── DisplayModule.cs                # Display automation implementation
└── config/
	├── homeassistant.conf              # Home Assistant configuration
	└── modes.json                      # Display modes configuration
```

## Dependencies

- **Microsoft.Extensions.Configuration.Ini** (10.0.9) - INI file configuration
- **Microsoft.Extensions.Hosting** (10.0.9) - Hosted service framework
- **Microsoft.Extensions.Http** (10.0.9) - HTTP client factory
- **Microsoft.PowerShell.SDK** (7.6.2) - PowerShell integration

## Logging

The service includes comprehensive logging via ILogger:

- Configuration validation messages
- State change events
- Display profile execution status
- Error and warning messages

Logs are written to configured output (console, event viewer, etc.)

## Security Considerations

- Store Home Assistant long-lived tokens securely
- Use HTTPS for Home Assistant connections in production
- Consider firewall rules to restrict service access
- Run service with minimal required privileges
- Keep configuration files protected with appropriate file permissions

## Advanced Configuration

### Modes Configuration (modes.json)

Define automation modes and associated programs in `config/modes.json`:

```json
{
  "modes": [
    {
      "name": "gaming",
      "displayConfig": "gaming",
      "programs": [
        {
          "command": "C:\\Games\\MyGame.exe",
          "arguments": "-fullscreen -high-quality -stream"
        },
        {
          "command": "C:\\Tools\\StreamOverlay.exe"
        }
      ]
    },
    {
      "name": "work",
      "displayConfig": "work-setup",
      "programs": [
        {
          "command": "Outlook.exe",
          "arguments": "/import"
        },
        {
          "sleep": 2
        },
        {
          "command": "C:\\Program Files\\Microsoft\\Teams\\Teams.exe",
          "arguments": "--profile-directory=C:\\Users\\User\\.config\\work"
        },
        {
          "command": "vpnclient.exe",
          "arguments": "connect AutoConnect"
        }
      ]
    },
    {
      "name": "movie",
      "displayConfig": "tv-mode",
      "programs": [
        {
          "command": "C:\\Program Files\\Kodi\\Kodi.exe"
        }
      ]
    }
  ]
}
```

### Configuration Options

**Mode Entry:**
- `name`: Unique identifier matching the Home Assistant entity state value
- `displayConfig`: Optional display configuration profile name to apply
- `programs`: Array of programs to launch or sleep instructions

**Program Entry:**
- `command`: Executable name or full path to launch
- `arguments`: Optional command-line parameters
- `delayAfterSeconds`: Legacy optional delay after program launch
- `sleep`: Sleep duration in seconds (use `"sleep": 5` instead of command/arguments)

### Display Configuration Profiles

Store display configuration files in `C:\ProgramData\Automation\Modes\` with `.xml` extension. The service imports serialized DisplayConfig objects that define monitor setups.

### Flexible Command Parsing

The service automatically parses command and arguments:
```json
// Method 1: Separate command and arguments
{
  "command": "notepad.exe",
  "arguments": "C:\\file.txt"
}

// Method 2: Combined in command (automatically split)
{
  "command": "notepad.exe C:\\file.txt"
}

// Method 3: Quoted paths with spaces
{
  "command": "\"C:\\Program Files\\App\\app.exe\" /parameter"
}

## Troubleshooting

### Service won't start
- Check configuration in `homeassistant.conf`
- Verify Home Assistant token and URL are correct
- Check application logs for validation errors
- Ensure the watched entity exists in Home Assistant

### Profiles/scripts not executing
- Verify PowerShell scripts/XML files exist in `C:\ProgramData\Automation\Modes\`
- Check script syntax and permissions
- Ensure the service has administrator privileges
- Review application logs for execution errors
- Check PowerShell execution policy settings
- Verify script files have correct extension (.ps1 or .xml)

### Programs not launching
- Verify `modes.json` exists and is valid JSON syntax
- Check program paths are correct and executable
- Verify command-line parameters are correct
- Ensure the service has permissions to execute the programs
- Review logs for error messages
- Test launching programs manually from command line
- Check that the mode name matches a configured mode in `modes.json`

### Connection issues
- Verify Home Assistant URL and network connectivity
- Check token hasn't expired or been revoked
- Review firewall and network configuration
- Check Home Assistant API is accessible

## Development

### Building from Source

```bash
dotnet build -c Release
```

### Running Tests

```bash
dotnet test
```

### Code Style

The project follows standard C# conventions with:
- Nullable reference types enabled
- Implicit usings enabled
- .NET 10 latest features

## License

See LICENSE file for details.

## Contributing

Contributions are welcome! Please ensure:
- Code follows existing style conventions
- Changes are documented
- Configuration validation is maintained
- Logging is appropriate for debugging

## Support

For issues and feature requests, please visit: https://github.com/toeck98/QoffeAutomat

---

**Project**: QoffeAutomat Service  
**Target Framework**: .NET 10  
**Author**: toeck98  
**Repository**: https://github.com/toeck98/QoffeAutomat
