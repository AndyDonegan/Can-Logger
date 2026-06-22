# CanLogger — CAN Bus Analyzer

A **GTK# 3 desktop application** written in **C# (.NET 8)** for monitoring and sending CAN bus frames on Linux. Uses raw **Linux SocketCAN** via P/Invoke for local hardware, or an **SSH pipe** from `candump` for remote CAN buses.

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
  - [Mode 2 — Remote CAN via SSH Pipe](#mode-2--remote-can-via-ssh-pipe)
- [GUI Walkthrough](#gui-walkthrough)
- [CAN Scheme CSV](#can-scheme-csv)
- [Permissions](#permissions)
- [Project Files](#project-files)
- [Troubleshooting](#troubleshooting)

---

## Features

- **Live CAN frame monitoring** — real-time table with timestamp, ID, DLC, data bytes, and frame type
- **Send single CAN frames** — specify hex ID and space-separated hex data bytes
- **Periodic frame transmission** — send a frame repeatedly at a configurable interval (ms)
- **CSV logging** — save all received messages to a `.csv` file
- **Dual backend support** — local SocketCAN hardware OR remote candump pipe over SSH
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
                    ┌──────────┴───────────────┐
                    │                          │
          ┌────────┴────────┐    ┌────────────┴──────────┐
          │   CanBackend     │    │ CandumpStdinBackend   │
          │  (SocketCAN raw) │    │  (SSH candump pipe)   │
          └────────┬────────┘    └───────────┬───────────┘
                   │                         │
          ┌────────┴────────┐     ┌──────────┴───────────┐
          │   CanSocket.cs  │     │  stdin regex parser  │
          │  P/Invoke libc   │     │  + ssh cansend       │
          └────────┬────────┘     └──────────────────────┘
                   │
          ┌────────┴────────┐
          │  Linux Kernel    │
          │  AF_CAN sockets  │
          └─────────────────┘
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
sudo ip link set can0 up type can bitrate 500000

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
2. Enter your CAN interface name (e.g. `can0`, `vcan0`)
3. Select the bitrate matching your bus
4. Click **▶ Start**
5. Incoming frames appear in the message table
6. Use the **Send CAN Frame** panel to transmit frames
7. Optionally start **Periodic** sending at a fixed interval
8. Click **📄 Log to File** to save a CSV log

### Mode 2 — Remote CAN via SSH Pipe

1. Launch: `ssh piZero candump can0 | dotnet run -- --stdin`
2. Click **Start** — the app begins reading candump output from the SSH pipe
3. Sending frames transmits via `ssh piZero cansend can0`
4. All other features (logging, watch list, scheme display) work identically

---

## GUI Walkthrough

```
┌──────────────────────────────────────────────────────────────┐
│  Interface: [can0]  Bitrate: [500000▼]  [▶ Start] [Clear]   │
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
│  Connected — can0 (logging)              Messages: 1,234     │
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

By default, CAN interfaces require root. To allow your user:

```bash
# Add a can group and your user to it
sudo groupadd can 2>/dev/null
sudo usermod -a -G can $USER

# Create a udev rule for CAN network interfaces
echo 'SUBSYSTEM=="net", KERNEL=="can*", GROUP="can", MODE="0660"' \
  | sudo tee /etc/udev/rules.d/50-can.rules

# Reload
sudo udevadm control --reload-rules
sudo udevadm trigger

# Log out and back in for the group to take effect
newgrp can   # or reboot
```

---

## Project Files

| File | Purpose |
|------|---------|
| `Program.cs` | GTK# GUI — window layout, event handlers, main entry point |
| `CanBackend.cs` | `ICanBackend` interface + SocketCAN backend (`CanBackend`) with background read loop |
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
| `bind() failed` | The interface may not be up: `sudo ip link set can0 up type can bitrate 500000`. |
| Permission denied | Follow the [Permissions](#permissions) setup above, or run with `sudo dotnet run`. |
| No frames appearing | Verify traffic exists: `candump can0` in another terminal. Check bitrate matches the bus. |
| GTK errors on startup | Install GTK 3 runtime: `sudo apt install libgtk-3-0`. |
| SSH pipe not working | Ensure `candump` is installed on the remote machine: `sudo apt install can-utils`. Test: `ssh piZero candump can0` (should show frames). |

---

## License

This project is provided as-is for educational and personal use. Use responsibly with CAN hardware you own or are authorised to access.

