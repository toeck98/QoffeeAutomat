# Wake-on-LAN

The integration adds a **Wake on LAN** button to the device of every PC. Pressing it starts the PC when it is asleep, hibernating
or switched off, and Home Assistant then connects to the agent as soon as the PC is up. A PC cannot be woken unless its
firmware and its network adapter are set up for it; this page lists what to check.

## How the button works

* The agent tells Home Assistant the MAC addresses of the network adapters of the PC. Home Assistant keeps them in the
  integration entry, so they are still known when the PC is off. The button appears once Home Assistant has connected to the agent
  for the first time (and after it learned the addresses, if the agent had none before).
* The button is **available while the agent is not connected**. That is the moment it is for: all other entities of the PC are
  unavailable then.
* A press sends one *magic packet* per stored MAC address, as a UDP datagram to port 9 of the broadcast address (see below).
* After a press Home Assistant tries to connect to the agent **once per second, for up to five minutes**, instead of backing
  off. The attempts end when the connection is up or the five minutes have passed. The *booted* event and the entities of the
  PC follow from the connection as usual.

The same packets can be sent from an automation, together with waiting and running a command: the action
`qoffeeautomat.run_command` has the fields `wake`, `wait_for` and `ready_timeout`, see
[Example: start the PC in a mode](example-mode-start.md#short-version-one-action-call). It sends the packets only when the PC is
not connected and not again while a wake-up is still running.

!!! note "A press is not a promise"
    Sending a magic packet cannot be confirmed: the network gives no answer, and a PC that is not set up for waking ignores it.
    The button only tells you that the packets were sent.

## The broadcast address

Open **Settings → Devices & services → QoffeeAutomat**, choose the PC and **Configure**. Enter the **Broadcast address**:

| Network of the PC | Broadcast address |
|-------------------|-------------------|
| Home Assistant and the PC are in the same network | The broadcast address of that network, for example `192.168.1.255` for `192.168.1.0/24`. |
| You are not sure | `255.255.255.255` (the default) reaches the network that Home Assistant is attached to. |
| Different networks (VLANs) | A router that forwards directed broadcasts or a Wake-on-LAN relay on the PC's network is needed; routers drop broadcasts by default. |

Home Assistant must be able to send a broadcast into the network of the PC. That is the case for Home Assistant OS, Supervised
and a container with host networking. A container on a bridge network cannot reach the LAN with a broadcast: use host
networking.

## Set up the PC

### 1. Firmware (BIOS or UEFI)

The names differ between manufacturers. Look for:

* **Wake on LAN**, **Power on by PCI-E / PCIe devices**, **Resume by LAN** or **Wake from S5**: enable it.
* **ErP / EuP Ready** (also *ErP Lot 3*): disable it. With it on, the mainboard cuts the power of the network adapter when the PC is
  switched off, and only waking from sleep works.
* **Deep Sleep**, **Deep S4/S5** (Intel boards): disable it.
* **Network stack** and PXE boot are not needed.

### 2. Network adapter in Windows

1. Open **Device Manager**, the network adapter (the wired one), **Properties**.
2. Tab **Power Management**: tick **Allow this device to wake the computer** and **Only allow a magic packet to wake the computer**.
3. Tab **Advanced**: set **Wake on Magic Packet** to *Enabled*. Set **Wake on pattern match** to *Disabled*. If there is an entry
   such as **Shutdown Wake-On-Lan** (Realtek), set it to *Enabled*. Features such as **Energy Efficient Ethernet** or **Green
   Ethernet** are a common reason for an adapter that wakes only now and then; try *Disabled*.

The same in PowerShell, as administrator (use the name from `Get-NetAdapter`):

```powershell
Get-NetAdapterPowerManagement -Name "Ethernet"
Set-NetAdapterPowerManagement -Name "Ethernet" -WakeOnMagicPacket Enabled -WakeOnPattern Disabled
powercfg /devicequery wake_armed
```

The last command lists the devices that may wake the PC; the adapter must be in the list.

### 3. Windows power settings

* **Fast startup** makes *Shut down* a kind of hibernation. On many adapters a PC in that state does not wake. In **Control Panel →
  Power Options → Choose what the power buttons do**, choose *Change settings that are currently unavailable* and untick
  **Turn on fast startup**.
* Wake from sleep (S3) is the most reliable; wake from shutdown (S5) depends most on the firmware settings above.

### 4. Cable and network

* Use a **wired** connection. Waking over Wi-Fi works on few PCs and needs *Wake on Wireless LAN* in the firmware and the Wi-Fi
  driver.
* The link light of the network port stays on (or blinks) while the PC is off when it is set up correctly.
* Some routers and switches with energy saving features turn the port off; disable it for the port of the PC.

## Test

1. Press **Sleep** or shut the PC down, wait a minute.
2. Press **Wake on LAN** in Home Assistant.
3. The PC should start within a few seconds, and the entities of the PC become available a moment after the agent is up.

If it does not wake, try a Wake-on-LAN tool from another device in the same network with the MAC address of the adapter. If that
does not wake it either, the problem is in the PC settings above or in the network; if it does, check the broadcast address in the
options and that Home Assistant can send broadcasts into that network.

## Security

A magic packet carries no password: every device in the network can wake the PC if it knows the MAC address. Waking a PC is
harmless, but do not rely on it as protection. Keep untrusted devices out of the network of the PC (guest network, VLAN).
