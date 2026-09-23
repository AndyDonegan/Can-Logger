# First in-app LIN receive test

Choose **Microchip APG LIN Analyzer (USB)** from **LIN analyzer** directly below
the CAN connection controls, leave **Initial baud** at **19600** for this EC600,
then press **Start LIN**. This opens **LIN data**
automatically; **Show LIN data** opens that view without connecting. The LIN
connection row stays visible while viewing CAN. Close Microchip's own
application first so only one application owns the analyzer. **Stop LIN** releases
it; starting/stopping CAN is independent. App shutdown also stops LIN.

This first stage uses a separate LIN table. Existing watch selection, CAN scheme,
CAN send panels and CAN file logging still apply to CAN only. The shared
CAN/LIN watch list, LIN file logging and LIN transmission remain later stages.
EC600 source definitions are now available in **EC600 LIN reference**; see
[the ID and byte reference](EC600-LIN-REFERENCE.md). The LIN table retains the latest 2,000 records and reports
any display-queue overflow. Clear LIN clears the displayed records.

## No administrator installation

Windows already supplies the HID driver for the connected Microchip USB device
`04D8:0A04`. The receiver runs on Windows while GTK runs in WSL. No USB passthrough,
UAC, driver replacement, system Java installation or firmware update is needed.
The dropdown currently offers the supported Microchip analyzer type, not a scan
of individual USB units. Connect one Microchip LIN analyzer at a time. Other
analyzer types can be added when their receive interfaces are implemented.
From this project's WSL directory, prepare/rebuild the optional helper with:

```sh
python3 scripts/setup-lin-receiver.py
dotnet build
DOTNET_ROLL_FORWARD=Major dotnet run --no-build
```

The runtime override is needed on this development machine because it has .NET
10 rather than .NET 8. A machine with .NET 8 installed can use `dotnet run` directly.

Setup fetches a pinned [Microchip TB3180 library archive](https://ww1.microchip.com/downloads/aemDocuments/documents/OTH/ApplicationNotes/AppnoteSourceCode/LIN_Library_API_Demo.zip)
and a pinned [Azul Zulu Java 8 FX Windows runtime](https://cdn.azul.com/zulu/bin/zulu8.96.0.205-ca-fx-jdk8.0.504-win_x64.zip),
verifies their SHA-256 hashes, and compiles our receiver. Downloads are cached in
`.vendor/microchip-lin/receiver/` (ignored by Git). The original Microchip demo
is never run: it contains transmit examples. Vendor notices remain in the cache;
redistribution of vendor binaries has not been authorized as part of this work.
The GUI installer in `install-microchip-lin.ps1` is not needed for this route.

## What the columns mean

- ID is the six-bit LIN ID (0–63); PID preserves the received parity bits.
- Reported baud is the library's per-record value. Microchip's
  [user guide, section 3.3.6](https://ww1.microchip.com/downloads/en/DeviceDoc/51675a.pdf)
  describes it as the rate measured during each frame's auto-baud detection.
  Section 3.5.1 describes the manual baud setting as applying to master
  transmissions. Our hardware tests nevertheless found that the initial adapter
  rate affects passive frame recognition. **Initial baud** offers 19600 (the
  reviewed EC600 source setting), 19200 and the original 10000 baseline.
  Auto-baud remains active; reported/readback rates can differ from the initial
  selection. Changing this setting sends USB configuration, not LIN frames.
  Error-marked records do not confirm the PSU's intended baud rate.
- Raw bytes include the checksum when present. Incomplete records keep every
  captured byte. **Last byte** shows the checksum candidate, and **Checksum result**
  tests it against all preceding bytes using classic and eligible enhanced checksums.
  A timeout/error record can show **Possible classic/enhanced match**; that is not
  proof of a complete frame. **No match (candidate)** means neither calculation
  matches, not proof that no checksum was transmitted. Empty records show
  **Unavailable (no bytes)**; one byte alone is **Not testable**. At least one data
  byte and one candidate checksum byte are needed; eight data bytes are not required.
  PID parity and receive errors remain visible even when a checksum matches.
- Data bytes excludes the checksum only for records the library reports complete.
  Classic/enhanced checksum and PID parity results are shown separately from
  receive errors. The expected checksum mode needs the future LIN definitions.
- Adapter time preserves the vendor timestamp; Received is the Windows host time.
  Do not assume the adapter clock is synchronized to CAN.

The helper performs USB configuration/status requests and continuous reception.
It does not request LIN headers, responses, wakeups, responder profiles or schedules.
USB health checks run every three seconds; helper failures are isolated from CAN.
An independent electrical trace has not been used to verify physical bus behaviour.

## Evidence and remaining checks

On this computer, the Windows helper opened the attached analyzer without admin
rights and received real records. Repeated start/stop/restart completed. Initial
records contained bus timeouts and some invalid PID parity, with reported baud
around 11,161–11,186. These are **not verified clean LIN frames**, and those baud
values must not be taken as the correct bus setting. Raw data is preserved for
comparison with Microchip's software.

An idle LIN responder normally needs another controller to send headers before it
will respond. This passive test does not supply those headers. Establish whether
the PSU has an active LIN controller, then compare traffic and baud/settings with
the previously working laptop setup. Physical unplug/replug, a reference capture,
and simultaneous live CAN/LIN traffic still require hardware checks.

Regression and hardware test commands:

```sh
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/LinReceive
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/LinReceive -- --hardware
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/LinReceive -- --ui
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/TooltipPlacement
```

The parser tests cover payload/checksum separation, checksum variants, parity,
partial/header-only records and malformed bridge records. Hardware tests perform
two receive sessions; the GTK test opens a temporary app window and exercises
LIN start/display/stop. Neither test sends LIN frames.

## Diagnosing reception that stops

The connection status independently shows successful USB health checks and elapsed
seconds since the last LIN record. “USB responding” proves status requests are
being answered; it does not prove the physical LIN bus is active or decoding is
correct. The UI keeps updating this status even when no new records arrive.

Each Start LIN writes a fresh `.vendor/microchip-lin/receiver/last-session.log`.
It contains UTC host timestamps, raw bridge records (including receive errors),
USB heartbeats and vendor diagnostics. It rolls over at approximately 2 MB; copy
it before restarting if preserving a particular capture matters. No signal names
or bit meanings are inferred from incomplete data.

During a 35-second diagnostic capture on 23 September 2026, one record arrived:
PID `0x34` (ID 52), reported baud 11161, raw bytes `10 10 10 F4`, vendor error 1
(bus timeout), also failing PID parity. Eleven later USB health checks succeeded,
without any further records. A live Java thread dump showed the receive thread
waiting in the USB read, not a Java deadlock. This does not establish whether the
bus stopped transmitting or the analyzer stopped decoding; compare with the
working Microchip setup and confirm whether another controller is polling the PSU.

## Received and expected PID / USB diagnostic checkpoint

The LIN table now displays **Received PID** and **Expected PID** beside the ID.
Expected PID is calculated from the received value's lower six bits; it is not a
second measurement from the PSU. ID 52 yields expected PID `180 / 0xB4`, whereas
received `52 / 0x34` fails parity. The raw value is never repaired or replaced.

The local trace now records complete raw HID input/output reports at the vendor
communication interface, before frame parsing, plus configuration snapshots before
and after the existing initialization. `CONFIG_BAUD` records the vendor API's baud
setting, not an independent wire-rate measurement. Output tracing also includes
USB control commands; those are not automatically LIN transmissions. HID input
buffers may retain trailing bytes from earlier reports: packet tags/lengths must
be respected when interpreting them.

The connection area displays adapter baud setting separately from receive rate,
and an input-report counter (including status reports). Summarize a saved trace:

```sh
python3 scripts/analyze-lin-trace.py .vendor/microchip-lin/receiver/last-session.log
```

A preserved 20-second trace is in the ignored local cache as `usb-diagnostic.tsv`.
It contains 90 input reports beginning with the vendor event-buffer tag `0x86`
over 19.680 seconds, plus 15 status reports beginning `0x88`. The library emitted
one timeout record, PID `0x34`, baud 11161, bytes `10 10 10 F4`. **88 event-buffer
reports arrived after that decoded record.** This supersedes any inference that
no incoming data remained after the displayed frame stopped. It does not yet
prove correct frame boundaries or valid electrical LIN traffic.

The vendor API reported configured baud 10000 after initialization. The EC600 V57B
source specifies 19600, but that is a different quantity from measured reception:
do not conclude that changing this setting alone fixes decoding. The preserved raw
trace is now available to examine break, sync, byte and auto-baud events.

The existing project `can_log.csv` has matching major-version evidence: CAN ID 53
contains `0x38` at byte index 2, and CAN ID 9 contains `0x38` at byte index 7, suggesting
version **56** under the reviewed layout. The log's PSU identity and capture date
have not been tied to the currently connected device, so this is not live firmware
identification. No P: drive files were written or changed for these diagnostics.


## Initial-rate experiment and status queue fix

A 15-second passive capture starting at 19600 produced 42 records continuously.
IDs 55 (PID 0x37) and 57 (PID 0x39) carried eight data bytes plus valid enhanced
checksums. IDs 56 (PID 0x78) and 58 (PID 0xBA) had valid header parity but no
response bytes; this is consistent with the PSU polling absent appliances.
The adapter subsequently reported 18939, which is its auto-baud result rather
than our requested initial setting. No command frames were sent.

Offline parsing of the original 10000 trace found 250 data-byte events but only
one break and one auto-baud event. The 19600 experiment contained 42 break and
42 auto-baud events. This supports an initial-rate decoding problem rather than
concluding that the PSU stopped transmitting. The reviewed firmware establishes
that the PSU supplies master headers. The earlier diagnostic uncertainty above
is retained as history; these observations supersede it.

Repeated tests also exposed a vendor-library status queue bug: status commands
remove the reply that the frame parser's There_Is_A_Status_Error subsequently
peeks at, crashing the receive thread with NullPointerException. Our helper uses
a status queue that retains the last actual snapshot for nonblocking peek/remove.
Timed poll still waits for a queued reply and can time out; no zero-filled status
or synthetic heartbeat is supplied. An offline helper check exercises this rule:
pass --test-status-queue to MicrochipLinReceiver. Vendor binaries are unchanged.
A 19200 experiment failed during initialization before this workaround, so it
provides no reception comparison at that rate.

After the workaround, the full GTK app test received 115 records over its
45-second start/display test (23 displayed at 12 seconds, 115 at 45 seconds),
including 58 valid enhanced-checksum responses. The preserved local trace
`verified-19600-ui.log` contains 115 break and auto-baud events. Two subsequent
12-second backend sessions each received over 10 records and stopped cleanly.
Parser, checksum display and status-queue checks passed. These tests used the
connected EC600 and no LIN transmissions; they do not establish compatibility
with every LIN device or exercise simultaneous live CAN traffic.

## EC600 reference display validation

The catalogue contains 13 IDs and 29 separately labelled layouts. Read-only source
hash/call-site auditing, payload binary rendering, unknown IDs, incomplete frames,
layout selection, GTK description columns and 84 tooltip sizing cases passed.
The live GTK start/display/stop retry received 24 rows. The first attempt failed
in the vendor status parser before its first status snapshot (NullPointerException);
this remains an intermittent startup limitation, separate from the reference UI.
The trace is retained locally as `reference-start-failure.log`. Stop/start retried
successfully; no automatic retry or receiver changes were added for this feature.

Auto heater integration tests passed using injected CAN/LIN records: CAN 133
still updates configuration when excluded by the watch list; setting changes
apply to new rows; historical rows preserve their original layout; queued old
settings are rejected after CAN session reset. Invalid, extended, error, short
and command frames are ignored. Unknown settings and setting 5 remain manual.
Live simultaneous CAN/LIN configuration detection has not yet been verified on
the attached PSU; no additional hardware commands were sent during these tests.
