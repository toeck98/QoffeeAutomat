# Hardware sensors (temperatures, fans, power draw)

The agent can read the **temperatures, fan speeds and power draw** of the processor, the graphics card, the mainboard and the drives
and offer them to Home Assistant as sensors. This is **optional and off by default**, because reading the processor and the
mainboard needs a **kernel driver**.

!!! warning "A kernel driver is involved"
    The sensors of the processor and the mainboard can only be read through the kernel driver **PawnIO**. A kernel driver runs with
    the highest rights that Windows has, so it deserves the same care as any other software that you install with administrator rights.

    * **You install the driver yourself**, separately from QoffeeAutomat, from its official source. The agent never installs,
      updates or removes a driver.
    * Install it only if you want these sensors. Drives and many graphics cards work without it.
    * Antivirus software may warn about hardware monitoring drivers.
    * While the setting is **off**, the agent loads neither the hardware library nor the driver, and does not even look whether
      the driver is installed.

## Turn it on

1. Open the agent UI and go to **Settings**.
2. Switch on **Read temperatures, fan speeds and power draw** under *Hardware sensors* and save.
3. Read the state under the switch. Opening the library takes a few seconds; use **Refresh status**.

| State | Meaning |
|-------|---------|
| Off | The setting is off. Nothing is loaded. |
| Starting | The hardware library is being opened. |
| Running | The sensors are read. |
| Running without the driver | PawnIO is not installed. Only sensors that need no driver (drives, most graphics cards) are read. The agent looks again every minute, so after you install PawnIO the processor and mainboard sensors appear without a restart. |
| Not running | The library could not be used; the UI shows the reason and the agent tries again every minute. |

## In Home Assistant

Every sensor is a metric with an id such as `hardware.intelcpu.0.temperature.0`. They are **not enabled by default**: a PC has dozens
of sensors, and Home Assistant creates no entity until you enable one on the device page. A sensor that nobody uses is not read.
The metrics are diagnostic entities; temperatures are in °C, fans in rpm and power draw in W.

The same ids can be listed in `prometheus.metrics` of the settings to include them in the Prometheus export.

## Turn it off

Switch the setting off and save. The agent closes the hardware library, which releases its connection to the driver, and removes the
sensors from the device. The driver stays installed, because it is yours; uninstall PawnIO with its own uninstaller if you do not
want it any more. The hardware library itself leaves the memory of the agent at its next restart.

## Troubleshooting

* **"Running without the driver" although PawnIO is installed.** The agent looks for the service `PawnIO`. Check that the installer
  finished and that `sc query PawnIO` answers. When the agent runs from a console that is not elevated, the library cannot open the
  driver either; the installed service runs with the rights it needs.
* **"Not running", message about the library version.** The agent refuses releases of the hardware library that load the old WinRing0
  driver, which is vulnerable. Update the agent.
* **A sensor shows nothing.** The library has no value for it (some fans report nothing when stopped). The entity is unavailable
  until it has one.
* **A drive is missing.** Drives that are asleep or behind some USB or RAID controllers do not report temperatures. The agent
  looks at the set of sensors every minute and picks up new ones.

Background and the decision behind it: `docs/adr/0002-hardware-sensors.md` in the repository.
