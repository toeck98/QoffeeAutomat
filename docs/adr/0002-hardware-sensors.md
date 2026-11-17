# ADR 0002: Hardware sensors (temperatures, fans, power draw) and the kernel driver they need

* Status: accepted with conditions. **Facts about third parties were not re-verified online** (see "Verification status"); the owner confirms them before the first release that contains the sensors.
* Task: T707 (PLAN.md S03 D-023, D-045, S16, S19)
* Code: `src/QoffeeAutomat.Platform.Windows/Metrics/Hardware/`
* Tests: `tests/QoffeeAutomat.Platform.Windows.Tests/Metrics/Hardware/` (`Category=Hardware`)

## Context

Home Assistant users ask for the temperatures, fan speeds and power draw of their PC. Windows does not expose them through a
supported API: processor and mainboard sensors sit behind model-specific registers and Super I/O chips, and reading them takes
**ring 0 access**, that is a kernel driver. D-023 therefore made them optional, off by default and a spike (this document) before any
code. The decision has to weigh three things:

* **Trust.** The agent runs as LocalSystem and a kernel driver runs with more rights than that. A driver that gives any process raw
  access to hardware is a privilege escalation for every standard user of the PC (compare D-017: no weakening of the security model
  for convenience).
* **Reputation.** Antivirus products and Microsoft Defender have flagged hardware monitors because of their drivers. An agent that is
  flagged is not installed.
* **Cost.** What is available without a driver should not be lost, and a PC that never turns the feature on must not pay for it.

Requirements: R-05 (system metrics), R-11 (quality of life). Constraints from AGENTS.md: no network calls to external hosts from the
product, no secrets in the repository, no silent change of a decision.

## Options considered

| Option | Assessment |
|--------|------------|
| A. **LibreHardwareMonitorLib** (NuGet `LibreHardwareMonitorLib`, library of the LibreHardwareMonitor project, licence **MPL-2.0**). | Chosen. Covers processor, graphics cards (NVIDIA, AMD, Intel), mainboard (Super I/O, embedded controllers) and drives (SMART, NVMe) in one managed library, and is maintained. The driver question is the whole risk; see "The driver" below. MPL-2.0 is file-level copyleft and fine for an unmodified library that is referenced as a package; see "Licence". |
| B. OpenHardwareMonitorLib (the ancestor, MPL-2.0). | Rejected: not maintained, and it uses the WinRing0 driver that A has left behind. |
| C. WMI / CIM only (`MSAcpi_ThermalZoneTemperature`, `Win32_Fan`, `Win32_TemperatureProbe`). | No driver, but firmware rarely fills these classes in: typically one unspecific thermal zone, no fan speeds, no power. Not a replacement. It stays available to users through the existing `cim_query` sensor type (T307). |
| D. Performance counters (`Thermal Zone Information`, `Processor Information`) and the GPU engine counters. | No driver, partial coverage (no fans, no per-core temperature). Stays available through `perf_counter` sensors; GPU load is already a built-in metric (`gpu.usage`, D-023). |
| E. Vendor command line tools (`nvidia-smi`, `smartctl`) from custom script sensors. | No driver of ours, full control for the user, and a good answer for a single GPU or drive. Stays available through script sensors; not a general solution. |
| F. A driver of our own. | Rejected: signing, maintenance and the attack surface of our own kernel code are out of proportion. |
| G. LibreHardwareMonitorLib in a **helper process** that the agent starts only while the feature is on. | The cleanest unload (the process ends and takes the library with it) and a crash of the library cannot take the service down. Deferred: it needs a second pipe protocol next to the session pipe. The seam `IHardwareSourceFactory` is where a helper process would plug in without touching the metrics. |

## The driver

> This section is what T707 asks to be named: which driver would be loaded and how far it is trusted.

**The agent loads exactly one driver, and only through the library: PawnIO** (service name `PawnIO`, written by namazso, installed by
the user with the installer from the PawnIO project, not by the agent). It is needed for processor and mainboard sensors. Drives, and
graphics cards for which the vendor provides a user-mode API, are read without it.

**Trust status: conditionally trusted, as a third-party, signed, open source driver with a smaller attack surface than its predecessor.**
The conditions are the "Conditions of the acceptance" below. In particular the agent

* never bundles, installs, starts, updates or removes a driver; the user installs PawnIO deliberately, after the UI has said so;
* never loads the **legacy WinRing0 driver** (below) and refuses to run a library version that does;
* does not look for the driver, and does not load the library, while the setting is off.

### The predecessor: WinRing0

Releases of LibreHardwareMonitorLib up to and including 0.9.4 carry the **WinRing0** driver (`WinRing0x64.sys`, service name
`WinRing0_1_2_0`) and load it as a kernel service. WinRing0 lets any process that can open its device read and write physical memory,
model-specific registers and I/O ports. It has known vulnerabilities (CVE-2020-14979 and CVE-2021-41285, local privilege escalation),
it was never fixed, and Microsoft Defender reports applications that load it (`VulnerableDriver:WinNT/Winring0`). **It is ruled out.**
Two guards keep it out, in `HardwareLibraryGuard`:

1. the version of the library must be at least 0.9.5 (`MinimumLibraryVersion`), otherwise the sensors do not start and the status says
   why; an unknown version is refused;
2. if the library creates the service `WinRing0_1_2_0` while opening, it is closed again at once and the sensors do not start.

### PawnIO

LibreHardwareMonitorLib 0.9.5 replaced WinRing0 with PawnIO. As far as it is known to the author of this record, PawnIO differs in the
ways that matter:

* it is a Microsoft-signed kernel driver, so Windows loads it without test signing or driver-signature changes;
* it does not offer raw physical memory, MSR or I/O port access. It runs small sandboxed modules (the library embeds the ones for the
  chips it supports) and a module can only do what it was written for, which is why a process that talks to the driver cannot read
  arbitrary kernel memory through it;
* it is open source; the driver and its modules have separate licences (GPL family and LGPL; to be confirmed);
* it is **not bundled** with the library: without the installer the library runs in a reduced mode.

It is still a kernel driver written by a single maintainer. Nothing below removes the need for the user to decide.

### Rights

The library opens the driver only from a process with administrator rights. The agent runs as LocalSystem, which has them. A developer
who starts the agent from a console that is not elevated gets the reduced mode (`driver_missing` or `failed` in the status).
The driver service is created by the PawnIO installer, which needs administrator rights once.

### Antivirus reputation

* WinRing0 based tools are reported by Defender and by other products; that is the reason for the guards above.
* A PawnIO based tool has fewer reports (to be confirmed) but the same *kind* of file: a hardware monitoring library. The agent ships
  `LibreHardwareMonitorLib.dll` in its folder **even when the setting is off**; it is only loaded when the setting is on. Before every
  release the owner scans the installer and the published folder (see the checklist) and records the result in the release notes.

## Decision

Add an **optional provider** in `Platform.Windows/Metrics/Hardware/`:

* The setting `hardwareSensors.enabled` in `settings.json` is **off by default** (absent means off). It has no other member.
* **Off means not loaded.** While the setting is off, `HardwareMetricProvider` describes no metric and has never called
  `IHardwareSourceFactory.Open`, so the library is not loaded and no driver handle exists. The agent does not even look whether the
  driver is installed. The production factory keeps every type of the library inside `LibreHardwareSource`, and `Open` is the first
  place that touches it (`[MethodImpl(NoInlining)]`), so creating the factory in the container does not load the assembly either.
  `HardwareSensorTests.With_the_setting_off_the_real_wiring_does_not_load_the_hardware_library` shows this with the production
  factory and probe by looking at the assemblies of the process.
* **On** opens the source in the background (opening takes seconds) and publishes the sensors as metrics:
  `hardware.<identifier of the sensor>`, for example `hardware.intelcpu.0.temperature.0`; the identifier is the one the library gives the
  sensor, so ids are stable. Only three kinds are exposed: temperature (`°C`, device class `temperature`), fan speed (`rpm`) and power
  draw (`W`, device class `power`), for the processor, graphics cards, mainboard (including Super I/O and embedded controller) and drives.
  Memory, network and the other hardware types are not enabled in the library.
* The metrics have `enabledDefault: false`: Home Assistant creates no entity until the user enables one, and a disabled entity costs
  nothing (D-007). The sensors are diagnostic entities with `state_class: measurement`.
* **Off again** disposes the source: the library closes every hardware part and releases its handle to the driver, and the metrics leave
  the manifest. The driver service stays installed (it is the user's). The managed assembly cannot be unloaded from the default load
  context; it leaves memory when the agent restarts. The UI says so. The helper process of option G would remove this caveat.
* The provider is a state machine, not a crash risk: `off`, `starting`, `running`, `driver_missing` (library open without the driver;
  checked again every minute, so a driver that is installed later is picked up), `failed` (the library cannot be used; tried again
  every minute) and `unsupported` (other platforms). `GET /api/v1/hardware-sensors` reports the state, the name of the driver
  and whether it is installed.
* The **UI** shows a warning about the kernel driver next to the switch, always, and the state below it (PLAN.md S16).

D-023 stays as the product decision; D-045 records this outcome.

### Conditions of the acceptance

1. Only PawnIO based releases of the library (>= 0.9.5); WinRing0 is never loaded (two guards above).
2. The agent never installs, updates or removes a driver. The documentation tells users to install PawnIO from its official source.
3. Off by default; nothing is loaded while it is off; the warning is shown in the UI and in the documentation.
4. The library is pinned in `Directory.Packages.props` and kept current by Dependabot; a release that brings a driver again is a decision
   for a new version of this record, not a routine update.
5. The owner completes the checklist below on a real Windows desktop (`human_verify`).

## Licence

LibreHardwareMonitorLib is MPL-2.0 (file-level copyleft, no effect on the other files of the agent as long as the library is used
unmodified, as it is here). The installer's third-party notices must name it, include the licence text and point to its source.
The PawnIO driver is not distributed with QoffeeAutomat, so its licence is the user's agreement with its project.

## Consequences

* A new package reference with its own dependencies (to be listed with `dotnet list package --include-transitive` and added to the SBOM,
  D-020).
* The agent folder grows by the library and its dependencies even for users who never turn the feature on.
* Tests use a fake source; the real library and driver are checked by hand.
* Dozens of sensors can appear on one PC. Because they are disabled by default, Home Assistant stays quiet until the user picks some.
* Sensors that need the driver are missing in `driver_missing`; which ones depends on the hardware.

## Verification status

The author of this record had **no network access**. The following are from memory of public information and **must be confirmed by the
owner**; where a point is wrong the decision may change (the guards and the setting do not).

| Statement | Where to check |
|-----------|----------------|
| LibreHardwareMonitorLib is MPL-2.0. | The package page on nuget.org and the repository's `LICENSE`. |
| 0.9.5 is the first release without WinRing0 and with PawnIO; the version in `Directory.Packages.props` (0.9.5) is **a floor, not the current release** and was not restored or built. | Release notes of LibreHardwareMonitor; look up the current stable version, pin it, restore. If the first PawnIO release has another number, change `HardwareLibraryGuard.MinimumLibraryVersion`. |
| The library's assembly or informational version equals the package version (the guard reads it). | `dotnet build`, then run on Windows with the setting on; the status says "older than" if it does not. |
| The PawnIO service is named `PawnIO` and is registered under `HKLM\SYSTEM\CurrentControlSet\Services\PawnIO`. | After installing PawnIO, look in the registry; adjust `PawnIoDriverProbe.ServiceName`. |
| PawnIO is Microsoft-signed, sandboxes its modules, has no known CVE, and is not on Microsoft's vulnerable driver blocklist. | The PawnIO project page and security advisories; the Microsoft recommended driver block rules; Windows Security "Core isolation > Microsoft Vulnerable Driver Blocklist" switched on. |
| PawnIO and WinRing0 licences and status as stated. | The projects' repositories. |
| Defender / antivirus reputation. | Scan `LibreHardwareMonitorLib.dll`, the published folder and the installer (for example with VirusTotal) before the release; record the result. |

### Checklist for the owner (`human_verify`)

- [ ] Look up and pin the current LibreHardwareMonitorLib; `dotnet restore`, `dotnet build -warnaserror`, `dotnet format --verify-no-changes`.
- [ ] `dotnet list package --include-transitive` for the new dependencies and their licences; add the notices to the installer.
- [ ] Windows PC without PawnIO, setting on: status `driver_missing`; drives and (NVIDIA/AMD) GPU temperatures appear; the warning text is shown in the UI.
- [ ] Install PawnIO from its official source; within a minute the status becomes `running` and processor, mainboard and fan sensors appear. Enable some in Home Assistant and compare with another tool.
- [ ] Setting off: sensors vanish from the manifest, Task Manager shows no further activity, and `sc query PawnIO` still shows the driver (it is the user's). With a tool such as Process Explorer, confirm that the agent has no handle to the PawnIO device any more.
- [ ] With the setting off from a fresh start: confirm in Process Explorer (DLL list) that `LibreHardwareMonitorLib.dll` is **not** loaded into the agent process; turn it on, confirm it is.
- [ ] No service `WinRing0_1_2_0` exists at any time (`sc query WinRing0_1_2_0`).
- [ ] Defender and the scan of the release artefacts are clean, or the reports are noted in the release notes.
