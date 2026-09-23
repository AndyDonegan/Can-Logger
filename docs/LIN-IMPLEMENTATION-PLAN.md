# Incremental LIN integration plan

Status: first in-app receive test implemented. See [LIN receive test](LIN-RECEIVE-TEST.md).
The initial test uses a separate LIN tab to validate the hardware path before
shared watch-list integration. Mixed protocol views and transmission remain planned.

## Agreed scope

Run the existing CAN connection and one LIN connection simultaneously. Initially
capture LIN frames passively, displaying their identifiers, actual payload length,
up to eight data bytes, timestamp and available error/checksum information. Add LIN
to the shared Watch List. Add user-supplied descriptions, byte/bit interpretation
and History later. LIN transmission is a separate later milestone.

## What is established and what needs confirmation

Read-only Windows enumeration confirmed a connected device reporting `LIN Bus
Analyzer`, USB VID `04D8`, PID `0A04`, status OK, using Microsoft's `HidUsb` driver.
It is a USB HID device, not a COM-port interface. The APG LIN name is consistent
with Microchip's APGDT001; confirm hardware revision/firmware against the vendor
library before treating every APGDT001 API capability as verified.

Microchip's [APGDT001 page](https://www.microchip.com/en-us/development-tool/apgdt001)
provides host software for Windows, Linux and macOS. Its
[TB3180 integration guide](https://ww1.microchip.com/downloads/aemDocuments/documents/OTH/ApplicationNotes/ApplicationNotes/TB3180-LIN-Analyzer-Library-Demo-90003180A.pdf)
describes a Java API, USB connection, continuous reception and frame metadata.
This is a candidate integration route, not a verified .NET-compatible driver.
Inspect the library, native dependencies and initialization behaviour before use;
the example also transmits frames, so it must not be run unchanged for passive capture.

The [analyzer user guide](https://ww1.microchip.com/downloads/en/DeviceDoc/51675a.pdf)
displays the protected identifier (PID), which includes parity bits, separately
from payload and checksum. Preserve the raw PID and normalized 6-bit ID. Separate
the checksum from the data bytes even if the library returns them in one buffer.
Confirm exact length, timestamp units and error semantics against the installed API.

The user confirmed the analyzer is connected to this Windows computer over USB
and to the PSU over LIN. It previously received data using Microchip's software
on a laptop. Current-machine vendor software availability and the LIN baud rate
remain unconfirmed. The proposed first transport is a Windows-side helper so
Windows retains ownership of the HID device; choose the supported SDK/runtime
after reviewing the installed/downloadable library. Avoid USB passthrough or driver
replacement unless the confirmed integration route requires it.
Passive reception needs an existing active LIN commander; an idle bus is not proof
that the reader is broken. Confirm bus power/common ground and monitor settings
against the unit's documentation. Do not enable a schedule, responder table,
wakeup transmission or master pull-up configuration as part of the receive test.

## Small delivery chunks and checkpoints

### 1. Identify the adapter and verify the host connection

Read USB identity and installed software/driver details. Locate the supported API
and dependencies. Verify baseline reception in Microchip's software, then close
that software before opening the adapter from our reader.

For the current WSL app, first assess a small Windows-side USB HID helper. Native
Linux access is an alternative if the Windows library route proves unsuitable.
The project already has
Windows helper patterns in `WaveshareWindowsBackend.cs` and
`ItekWindowsBackend.cs`; their lifecycle pattern is reusable, but the LIN helper
may need Java rather than .NET. USB access is resolved before UI restructuring.

Done when: the exact adapter/driver path is identified, the library can open/close
it reliably, and passive-mode initialization is documented. Report a precise
blocker if the device or API is unavailable; do not assume hardware compatibility.

### 2. Receive-only LIN reader — first hardware test point

Build a small standalone diagnostic/helper that opens the adapter, starts passive
capture and emits structured records. Keep stdout machine-readable and diagnostics
on stderr. Preserve repeated frames; provide stop, disconnect and error reporting.
Use the actual payload length, not an assumed fixed eight bytes. Keep incomplete
frames/header-only events identifiable rather than padding them into valid frames.
No application-level LIN transmit path is enabled in this chunk.

Done when: capture real traffic and compare ID/PID, byte order, lengths, checksum
and repeat counts with Microchip's software or the same controlled fixture. Verify
capture stop/restart, USB unplug and an idle bus. Confirm no LIN transmissions are
requested by reader initialization or capture, ideally with an independent trace.
Save a small capture fixture for repeatable parser tests. No GUI changes are needed
for this first hardware test.

### 3. Add protocol identity and exercise the UI using replay

Keep `ICanBackend` and existing CAN transports working. Introduce `LinMessage` and
a receive-only LIN interface, plus a shared display/log envelope carrying protocol,
source, host receive time and optional adapter timing/error metadata. Adapter clocks
must not be assumed synchronized; initially order by host reception and document it.

Add a visible CAN/LIN column to the message view. Change shared row identity,
watch filtering and description lookup to distinguish `(protocol, ID)`; CAN ID 12
and LIN ID 12 are different entries. Retain existing CAN frame flags and errors.
LIN row clicks must never populate or trigger the existing CAN transmit panels.
Replay LIN fixtures through this path while running CAN regression checks.

Done when: mixed replay displays correctly, same-number IDs cannot collide,
existing CAN send controls still work only on CAN rows, and tooltips use the
correct protocol. Existing CAN-only operation remains available without LIN libraries.

### 4. Connect LIN to the app — first simultaneous CAN/LIN test

Add independent LIN adapter/baud selection, Start/Stop, connection status and frame
counts. Starting or stopping either bus must leave the other running. App shutdown
must close both. Adapter failures and helper-process exits stay isolated.

Group/label Watch List entries as CAN or LIN, and add a protocol selector to the
ad-hoc entry. No selected IDs means show all for that protocol, so existing CAN
selections do not hide newly connected LIN traffic. Show unknown LIN frames before
any LIN scheme exists. Display raw PID alongside normalized LIN ID to make comparison
with Microchip straightforward. Preserve LIN errors even when data is incomplete.

Keep CAN-only CSV export compatible; introduce a documented combined export with
protocol/source, ID, PID, timestamp, length, payload and error/checksum fields.
Preserve current selection-based logging behaviour and apply filters per protocol.
Use bounded UI delivery with observable overflow/error counts so concurrent streams
cannot silently overwhelm the GTK event queue.

Done when: real CAN and LIN frames arrive together; protocol filtering, ad-hoc IDs,
combined logging and tooltips work; same-ID traffic stays separate; stopping or
unplugging LIN does not interrupt CAN; all existing CAN workflows pass regression.

### 5. Import LIN definitions and add interpretation

When supplied, inspect the LIN information file before designing its conversion.
Use a separate LIN scheme and preserve History. Keep byte index separate from bit
index/range: the existing CAN CSV's `Bit` column actually represents a byte index.
Agree bit numbering, multi-byte byte order, scaling and enumerations where needed.
Use the same tooltip/Info presentation, including screen-boundary regression checks.

### 6. Add LIN commands in a separate change

Design a LIN-specific send area. LIN transmission involves commander headers,
published responses and timing, rather than simply reusing CAN Send. Establish the
intended bus role, checksum rules and scheduling before implementation. Keep passive
capture as the default, with an explicit transition to active operation.

## Delivery discipline

Initial discovery is partially complete: Windows USB identity and HID driver are
confirmed. A subsequent normal-user access probe successfully opened a read-only
handle without elevation and obtained 65-byte input/output report capacities.
It closed the handle without reading frames or sending any device/LIN commands.
Administrator installation of the vendor GUI is not required for this access path.
See `scripts/test-microchip-lin-access.ps1`. Passive initialization and frame decoding
remain to be verified before the first receive test.

Use a separate reviewable commit for each chunk. Mark software/replay checks and
real hardware checks separately. Complete the receive-only diagnostic milestone
before integrating live LIN into the GUI; complete simultaneous reception before
adding decoding or transmission. The next implementation task is chunk 1.
