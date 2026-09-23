# EC600 LIN / CI-Bus source findings

Reviewed 23 September 2026. Shared-drive files were read only; no PSU firmware,
configuration or bus transmissions were changed.

## Sources and applicability

Folder: `P:\03 Design Files\04 Projects\EC600\Flowcode`.

- `EC600PSU V57B.fcfx` and generated `EC600PSU V57B.c`: newest matching top-level
  PSU source found in the listing. The generated C header dates it 22 September
  2026. Main initialization assigns `PSUSerial = 57`, `ModelYear = 27`.
- `EC600PSU V55V - new CI.c`: earlier comparison. The extracted SendHeader,
  SendByte and SendMessageWithData function bodies match V57B exactly.

This establishes what these source versions implement, not which firmware is on
the connected PSU, nor which build has been formally released. Full proprietary
sources were kept in temporary local review copies, not added to this repository.

## Bus role, framing and baud

The PSU uses Flowcode's `LinMaster1` component. It sends LIN headers, sends some
responses itself, and requests responses from attached CI-Bus appliances. It is
not simply a passive responder waiting for the analyzer to poll it.

The actual `LinMaster1` component properties in V57B.fcfx are:

| Property | Value |
| --- | --- |
| BAUD | 19600 |
| pBitTime | 51 microseconds |
| pBitHalfTime | 25 microseconds |
| pBit13Time | 663 microseconds |
| TX / RX | PORTE.1 / PORTE.0 |
| CS / WAKE | PORTE.2 / PORTD.0 |

These are MCU signals, not external connector pin numbers.
`SendByte` generates bits in software and checks the receive echo during each bit.
`SendHeader` generates the break, delimiter, sync byte `0x55`, then the protected
identifier. The generated C uses an 8 MHz CPU setting. Actual wire timing includes
instruction overhead and depends on the running build and clock; 19600 is the
configured source value, not a measurement. An older project history note says
19200; do not mistake that historical note for the current property. The unrelated
RS232 component has a 9600 baud setting, which is not the LIN setting.

Our captured reported baud values near 11161–11186 do not line up with that source
configuration. This is a reason to investigate timing/decoding and confirm the
installed firmware, not proof of a particular fault or an instruction to force
an unverified receiver setting.

## PID parity

`FCD_07e91_LinMaster1__SendHeader` masks the input ID to six bits and calculates
standard LIN P0/P1 before sending it. It does not put application data in the
upper two bits.

The actual extracted firmware routine was executed locally with GPIO/delay/send
calls stubbed out. All 64 identifiers matched independently calculated standard
parity. ID 52 produces PID `0xB4`, not `0x34`. No hardware was accessed by this test.

This contradicts the hypothesis that these source versions simply send bare IDs.
It cannot prove what the connected PSU physically transmitted. An ID/PID reported
as `0x34` can still result from receive interpretation, sampling, wiring, a different
firmware build, or a signal problem.

## Checksum and response format

`SendMessageWithData` accepts 1–8 data bytes and appends an inverted end-around-carry
checksum. Classic starts at zero; enhanced starts with the protected identifier.
IDs above 59 use classic on the transmit path. `SendMessage` receives the requested
number of bytes, then receives and checks a separate checksum byte.

`FCM_SendIDAndRxData` requests enhanced checksum; its `Classic` variant requests
classic. The inspected appliance transactions below use eight data bytes, followed
by a checksum, but that does not make shorter LIN responses invalid in general.

The subsequent full call-site inventory and reviewed byte/bit layouts are in
[EC600 LIN reference](EC600-LIN-REFERENCE.md), with an [ID table CSV](EC600-LIN-IDS.csv).
That reference distinguishes active ATC data from the disabled Alko2/LEVC experiment.

## Useful source examples (not a complete signal dictionary)

These IDs are reused according to appliance selection. They are not universal
commands for every EC600 installation.

| ID decimal / hex | Direction in example | Example source routine / use | Checksum |
| --- | --- | --- | --- |
| 8 / 08 | PSU sends data | AirconCTRL / TAircon / DAircon, air-conditioning control | Enhanced |
| 11 / 0B | PSU sends data | FridgeCTRL / FridgeINFO, refrigerator control | Enhanced |
| 12 / 0C | PSU requests appliance data | FridgeINFO | Enhanced |
| 23 / 17 | PSU requests appliance data | AirconINFO | Enhanced |
| 26 / 1A | PSU sends data | AldeINFOCTRL / AldeINFOCTRL9 | Enhanced |
| 27 / 1B | PSU requests appliance data | AldeINFOCTRL / AldeINFOCTRL9 | Enhanced |
| 36 / 24 | PSU requests appliance data | Alko2 | Enhanced |
| 55 / 37 | PSU sends data | Truma / Whale control routines | Enhanced |
| 56 / 38 | PSU requests appliance data | Main-loop appliance polling branches | Enhanced |
| 57 / 39 | PSU sends data | Truma / Whale / Webasto / Eberspacher routines | Enhanced |
| 58 / 3A | PSU requests appliance data | Main-loop heating polling branches | Enhanced |
| 60 / 3C | PSU sends diagnostic request data | AldeDiag / TrumaDiag / FreshjetNAD | Classic |
| 61 / 3D | PSU requests diagnostic response | Corresponding diagnostic routines | Classic |

Payloads include actual appliance controls and configuration operations. Do not
replay arbitrary payloads merely because the identifiers are now known. For
future communication with the PSU on LIN, implement the selected appliance's
response to a PSU header; do not assume the analyzer should become a second master.

## Why traffic may be sparse

The timer advances LIN scheduling after a short startup delay. The active main
loop checks `LINOn`, `LinReadReady`, and `DimmerRunning`. It chooses different
polling slots with engine state and configured appliance selections (`SetOut3`).
The regular slot counter cycles through 0–11; engine-mode through 0–30. A newer
`FCM_LIN_Polling` routine exists but its call in main is inside `#if 0`: it must not
be mistaken for the active schedule.

Missing appliance replies can explain response timeouts. The reviewed scheduler
continues cycling; this review did not establish a general rule that it permanently
stops all LIN traffic after unanswered requests. Enabled devices and other state
must be checked against the actual PSU configuration. No automatic reply is
required from a passive monitor.

## Identify the running version without changing the PSU

In this source, the existing CAN output includes `PSUSerial` (set to 57) in:

- CAN ID 9 (`0x009`): data byte index 7, the eighth byte.
- CAN ID 53 (`0x035`): data byte index 2, the third byte.

A captured value of decimal 57 / hex `0x39` would support major version 57 if the
installed build uses this layout. These fields do not distinguish revision A/B.
Read them from existing traffic first, rather than changing settings or sending
commands to discover the version.

## Next diagnostic checkpoint

1. Confirm firmware major version and configured CI-Bus appliances through existing
   CAN traffic or the normal system configuration display.
2. Compare a passive capture on a normally populated, operating CI-Bus network with
   the standalone-PSU capture. Correlate the expected protected identifiers above.
3. Check receiver configuration and raw USB event interpretation against the vendor
   tool. If needed, measure break/sync/PID on the wire to distinguish a decoding
   problem from actual transmission problems.
4. Only after reliable reception, implement one known appliance response with its
   correct payload, length and checksum. No polling or response code was added by
   this source review.

## Heater selection and the Webasto / Whale naming conflict

CAN ID 133 (0x85), payload byte 2, reports SetOut3[2]. The receive command ID
173 is not treated as confirmation. Reviewed firmware mappings are 0=None,
1=Alde, 2=Truma CP+, 3=Whale, 4=Eberspacher, 5=Webasto LIN.
Whale5 checks value 3 at line 58693; WebastoLIN checks value 5 at line 53825.
The main-loop call at 72908 (also 73006, 73104, 73216) is not inside a disabled
preprocessor block. The setting-5 polling branch requests LIN ID 58 at 71129.
Thus the source still has a callable Webasto LIN implementation; production
usage and control-panel menu availability cannot be inferred from that alone.

A separate WebastoPWM routine checks WPEnable (67208 onwards), uses physical
outputs/PWM, accepts CAN 188 settings (17525 onwards), and reports CAN 148
(67618). CAN settings traffic does not by itself prove the heater's physical
connection is directly CAN. This separate path does not remap CI-BUS setting 5
to Whale. The current CAN CSV says setting 5 is Whale Ci-Bus, conflicting with
this generated firmware; that CSV entry has not been silently changed.

Auto heater layout selection uses only complete standard, non-error CAN 133
reports and values 2/3/4. Setting 1 requires manual Alde generation selection;
setting 5 stays ambiguous/manual pending authoritative confirmation. It runs
before the CAN watch filter, does not send configuration messages, resets on
CAN stop/start/backend change, and retains the selection/evidence in each LIN
row so a later configuration change cannot relabel history. Manual hover and
full-detail layout selections remain available. Payload patterns alone are not
used to infer the appliance make.
