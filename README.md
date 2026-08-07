# CanLogger — CAN Bus Analyzer

A **GTK# 3 desktop application** written in **C# (.NET 8)** for monitoring and sending CAN bus frames. It supports raw **Linux SocketCAN**, the Windows-only **Waveshare USB-CAN-FD** through a WSL-to-Windows bridge, and an **SSH pipe** from `candump` for remote CAN buses.

---

## Table of Contents

- [Features](#features)
- [Technology Stack](#technology-stack)
- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Hardware Setup](#hardware-setup)
- [Quick Test (Virtual CAN)](#quick-test-virtual-can)
- [Build & Run](#build--run)
- [Usage](#usage)
  - [Mode 1 — Local CAN Hardware](#mode-1--local-can-hardware)
  - [Mode 2 — Waveshare USB-CAN-FD](#mode-2--waveshare-usb-can-fd)
  - [Mode 3 — Remote CAN via SSH Pipe](#mode-3--remote-can-via-ssh-pipe)
- [GUI Walkthrough](#gui-walkthrough)
- [CAN Scheme CSV](#can-scheme-csv)
- [Permissions](#permissions)
- [Project Files](#project-files)
- [Troubleshooting](#troubleshooting)

---

## Features

- **Live CAN frame monitoring** — real-time table with timestamp, ID, DLC, data bytes, and frame type
- **Three independent send frames** — configure and transmit three different messages, each with its own periodic timer
- **Colour-coded click-to-reuse frames** — click, Shift-click, or Ctrl-click received rows to populate send frames 1, 2, or 3
- **Periodic frame transmission** — send a frame repeatedly at a configurable interval (ms)
- **CSV logging** — start and stop recording with a live filename, elapsed-time, and frame-count indicator
- **Three CAN backends** — local SocketCAN, Waveshare USB-CAN-FD via its Windows API, or remote candump over SSH
- **CAN scheme file** — load a CSV defining CAN IDs, descriptions, and per-byte meanings
- **Watch List filtering** — tick individual CAN IDs to filter the message view in real time
- **Byte-level info** — inspect individual bytes of a selected message with variable/function details
- **Lock scroll** — freeze the message view while inspecting older frames
- **Hex/Dec toggle** — switch between hexadecimal and decimal display for ID and data columns
- **Row tooltips** — hover over any message row for a byte-level breakdown

---

## Technology Stack

| Layer | Technology |
|-------|-----------|
| **Language** | C# 12 (.NET 8) |
| **GUI Framework** | GTK# 3 (GtkSharp 3.24) |
| **CAN (local)** | Linux SocketCAN — raw socket P/Invoke (`AF_CAN`, `socket()`, `bind()`, `read()`, `write()`) |
| **CAN (Waveshare)** | Windows `ControlCANFD.dll` bridge launched directly from WSL |
| **CAN (remote)** | SSH pipe — parses `candump` stdout via regex |
| **Build system** | .NET SDK (`dotnet build` / `dotnet run`) |
| **Platform** | Linux (x64 / arm64) |

---

## Architecture

```
                    ┌──────────────────────────┐
                    │     Program.cs (GTK#)     │
                    │   GUI + Application Logic │
                    └──────────┬───────────────┘
                               │ ICanBackend
             ┌─────────────────┼────────────────────┐
             │                 │                    │
     ┌───────┴────────┐ ┌──────┴──────────┐ ┌──────┴────────────┐
     │   CanBackend    │ │ WaveshareWindows│ │CandumpStdinBackend│
     │ (SocketCAN raw) │ │    Backend      │ │ (SSH/stdin pipe)  │
     └───────┬────────┘ └──────┬──────────┘ └──────┬────────────┘
             │                 │                    │
     ┌───────┴────────┐ ┌──────┴──────────┐ ┌──────┴────────────┐
     │ Linux AF_CAN   │ │ Windows vendor  │ │ candump parser +  │
     │ sockets        │ │ API bridge      │ │ ssh cansend       │
     └────────────────┘ └─────────────────┘ └───────────────────┘
```

Supporting files:
- **`CanMessage.cs`** — immutable data record for a single CAN frame
- **`CanScheme.cs`** — loads and parses the `can-scheme.csv` ID definition file
- **`CanSchemeDialog.cs`** — the Watch List / Info dialog widget

---

## Prerequisites

- **.NET SDK 8.0** (or later)
- **Linux** with either:
  - A CAN adapter and SocketCAN support, **or**
  - WSL2 on Windows with the Waveshare USB-CAN-FD WinUSB driver and Windows .NET 8, **or**
  - SSH access to a machine running `candump` / `cansend`
- **GTK 3 runtime** (usually pre-installed on desktop Linux):
  ```bash
  sudo apt install libgtk-3-0
  ```

---

## Hardware Setup

### CAN Adapters

You need a USB-to-CAN adapter. Well-supported options:

| Adapter | Kernel Driver | Price |
|---------|--------------|-------|
| Canable (candleLight) | `gs_usb` | ~$30 |
| InnoMaker USB2CAN | `gs_usb` | ~$20 |
| Peak PCAN-USB | `peak_usb` | ~$250 |
| Kvaser Leaf Light | `kvaser_usb` | ~$300 |
| Waveshare USB-CAN-FD | Windows WinUSB/vendor API bridge | — |

### Wiring

A CAN bus requires 3 wires and termination:

```
[PC] --USB--> [CAN Adapter] --CAN_H──┬──CAN_H── [Device]
                         --CAN_L──┼──CAN_L──
                         --GND────┘
                                  
                          120Ω at each end
```

> ⚠️ Most adapters have a jumper or switch for the 120Ω termination resistor — enable it if your adapter is at the end of the bus.

### Bringing the Interface Up

After plugging in the adapter:

```bash
# Check it appeared
dmesg | tail -20
ip link show

# Bring it up at the desired bitrate
sudo ip link set can0 up type can bitrate 125000

# Verify
ip -details link show can0
```

Common bitrates: `10000`, `20000`, `50000`, `100000`, `125000`, `250000`, `500000`, `800000`, `1000000`.

---

## Quick Test (Virtual CAN)

No hardware? Use a virtual CAN interface to test the app immediately:

```bash
sudo modprobe vcan
sudo ip link add dev vcan0 type vcan
sudo ip link set up vcan0
```

In another terminal, generate test traffic:
```bash
cangen vcan0
```

Then run the app and connect to `vcan0`.

---

## Build & Run

```bash
cd CanLogger

# Restore NuGet packages
dotnet restore

# Required once for Waveshare USB-CAN-FD support. This downloads the official
# x64 ControlCANFD.dll into the ignored local .vendor directory.
./scripts/install-waveshare-api.sh

# Build
dotnet build

# Run (local CAN hardware)
dotnet run

# Run (SSH pipe mode — remote CAN bus)
ssh piZero candump can0 | dotnet run -- --stdin
```

The `--stdin` flag switches to the `CandumpStdinBackend`, which parses `candump`-formatted lines from standard input and sends frames via `ssh <host> cansend <iface>`.

---

## Usage

### Mode 1 — Local CAN Hardware

1. Launch: `dotnet run`
2. Select a detected CAN interface (e.g. `can0`, `slcan0`, or `vcan0`), or enter its name manually. Use **Refresh** after plugging in an adapter.
3. Select the bitrate matching your bus. `125000` is one of the available choices.
4. Click **▶ Start**
5. Incoming frames appear in the message table
6. Click a received frame to copy it into blue **Send CAN Frame 1**, Shift-click one for amber **Frame 2**, or Ctrl-click one for green **Frame 3**. Each source row remains highlighted in the matching colour, and each panel's Hex/Decimal toggle controls its copied format.
7. Use any of the three **Send CAN Frame** panels to transmit independent messages.
8. Optionally start **Periodic** sending at a fixed interval
9. Click **📄 Log to File** to save a CSV log

Logging remains active if the CAN stream is stopped or restarted, so one CSV can
cover multiple capture sessions. Click **■ Stop Logging** to finish and close the
file; closing the application also closes any active log safely.

USB adapters supported by Linux SocketCAN do not need an adapter-specific option in
the app: their kernel driver exposes them as a CAN network interface, normally
`can0` or `can1`. The app applies the selected bitrate when Start is clicked. If the
interface is already up at that bitrate, no elevated permission is needed. Changing
the interface configuration requires root or `CAP_NET_ADMIN`; if that is unavailable,
the app displays the equivalent `sudo ip link` commands to run once in a terminal.

The bitrate control is disabled in `--stdin` mode because the CAN interface belongs
to the remote machine and must be configured there.

### Mode 2 — Waveshare USB-CAN-FD

1. Install Waveshare's Windows driver and confirm Device Manager shows a healthy **WinUSB Device**.
2. Close `CANFDToolPro`; the vendor API only permits the analyser to be opened once.
3. Run `./scripts/install-waveshare-api.sh`, then `dotnet build`.
4. Launch CanLogger in WSL with `dotnet run`.
5. Select `waveshare-can1` or `waveshare-can2`, select `125000`, and click **Start**.

The bridge accesses the device on Windows directly, so USB/IP attachment is not
required. It initializes the analyser through its CAN-FD controller API and monitors
both vendor receive queues, which is also required to receive ordinary CAN 2.0 frames
on this model. Sending is currently limited to classic CAN frames up to 8 data bytes;
a separate CAN-FD data-phase bitrate is not yet exposed in the UI.

### Mode 3 — Remote CAN via SSH Pipe

1. Launch: `ssh piZero candump can0 | dotnet run -- --stdin`
2. Click **Start** — the app begins reading candump output from the SSH pipe
3. Sending frames transmits via `ssh piZero cansend can0`
4. All other features (logging, watch list, scheme display) work identically

---

## GUI Walkthrough

```
┌──────────────────────────────────────────────────────────────┐
│  Interface: [can0▼] [Refresh]  Bitrate: [125000▼]  [▶ Start] [Clear] │
│  [📄 Log to File]  [✓ Lock scroll]                          │
├──────────────────────┬───────────────────────────────────────┤
│  Watch List          │  #  Timestamp   ID    DLC  Data ...  │
│  ┌────────────────┐  │  1  12:00:01.234  0x123  8  A1 B2.. │
│  │ ☑ 0x008 (0x8)  │  │  2  12:00:01.456  0x7DF  3  02 01.. │
│  │ ☐ 0x123 (0x53) │  │  3  12:00:01.678  0x200  5  11 22.. │
│  │ ☑ 0x7DF (0x7DF)│  │  ...                                  │
│  │ ...             │  │                                       │
│  │ [All] [None]    │  │                                       │
│  └────────────────┘  │                                       │
│  [Info]              │                                       │
├──────────────────────┴───────────────────────────────────────┤
│  Send CAN Frame                                              │
│  ID (hex): [7DF]  Data: [02 01 00...]  [☐ Extended ID]     │
│  [Send]  │  Periodic (ms): [1000]  [Start Periodic]         │
├──────────────────────────────────────────────────────────────┤
│  Connected — can0     ● RECORDING — can_log.csv — 1,234 frames │
└──────────────────────────────────────────────────────────────┘
```

**Key controls:**

| Control | Action |
|---------|--------|
| **Watch List** | Tick/untick CAN IDs to filter the message table. Only ticked IDs are shown. |
| **All / None** | Select or clear all IDs in the watch list. |
| **Info** | Select a message row and click Info for a per-byte breakdown. |
| **Lock scroll** | Freezes the table scroll position so you can inspect older messages. |
| **Description column** | Shows the purpose of each CAN ID, loaded from `can-scheme.csv`. |
| **Hex/Dec toggle** | Switches ID and data columns between hex and decimal display. |

---

## CAN Scheme CSV

The file `can-scheme.csv` defines CAN IDs, their descriptions, and per-byte meanings. Format:

| Column | Description |
|--------|-------------|
| `CanID` | CAN arbitration ID (decimal) |
| `Description` | Human-readable purpose of this ID |
| `Bit` | Byte index within the frame (0 = first data byte) |
| `Variable` | Name of the variable at this byte position |
| `Function` | What this byte controls/represents |
| `Options` | Enumeration of possible values |

The app loads this file at startup and uses it to:
- Populate the **Watch List** with all known IDs
- Show a **Description** column in the message table
- Provide **byte-level tooltips** on hover
- Power the **Info** dialog for detailed inspection

---

## Permissions

The app can normally open an interface that is already configured and up without
running as root. Changing its bitrate or bringing it up requires Linux
`CAP_NET_ADMIN`. The recommended setup is to configure it once in a terminal:

```bash
sudo ip link set dev can0 down
sudo ip link set dev can0 type can bitrate 125000
sudo ip link set dev can0 up
```

After that, launch the app as your normal user and select `can0` / `125000`. If the
app detects that the interface is already up at the selected bitrate, it does not
run any privileged configuration command.

---

## Project Files

| File | Purpose |
|------|---------|
| `Program.cs` | GTK# GUI — window layout, event handlers, main entry point |
| `CanBackend.cs` | `ICanBackend` interface + SocketCAN backend (`CanBackend`) with background read loop |
| `CanInterfaceManager.cs` | Detects SocketCAN interfaces and applies the selected local bitrate |
| `WaveshareWindowsBackend.cs` | Runs and communicates with the Windows vendor-API bridge from WSL |
| `WaveshareNative.cs` | Waveshare API declarations, Windows bridge loop, receive and transmit framing |
| `CandumpStdinBackend.cs` | Stdin pipe backend — parses `candump` lines via regex, sends via `ssh cansend` |
| `CanSocket.cs` | Low-level Linux SocketCAN P/Invoke (`AF_CAN` raw sockets — `socket`, `bind`, `read`, `write`) |
| `CanMessage.cs` | Immutable `record` for a CAN frame (timestamp, ID, DLC, data, flags) |
| `CanScheme.cs` | CAN bus scheme loader — parses `can-scheme.csv` into `CanIdDef` / `ByteDef` objects |
| `CanSchemeDialog.cs` | Watch List panel widget + Info dialog with per-byte details |
| `can-scheme.csv` | CAN ID definitions shipped with the application |
| `CanLogger.csproj` | .NET 8 project file with GtkSharp NuGet reference |

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| `socket() failed` | Check that the CAN interface exists: `ip link show`. Load kernel module if needed: `sudo modprobe can`. |
| `bind() failed` | The interface may not be up: `sudo ip link set can0 up type can bitrate 125000`. |
| Permission denied while configuring | Run the three commands in [Permissions](#permissions), then launch the app normally. |
| No frames appearing | Verify traffic exists: `candump can0` in another terminal. Check bitrate matches the bus. |
| Waveshare cannot open | Close `CANFDToolPro`, confirm the WinUSB device is healthy, and rebuild after running `scripts/install-waveshare-api.sh`. |
| Waveshare opens but receives 0 frames | Check the selected CAN1/CAN2 channel, connect H-to-H, L-to-L and GND, confirm 125 kbit/s and classic CAN mode, and check bus termination/activity. |
| GTK errors on startup | Install GTK 3 runtime: `sudo apt install libgtk-3-0`. |
| SSH pipe not working | Ensure `candump` is installed on the remote machine: `sudo apt install can-utils`. Test: `ssh piZero candump can0` (should show frames). |

---

## License

This project is provided as-is for educational and personal use. Use responsibly with CAN hardware you own or are authorised to access.
