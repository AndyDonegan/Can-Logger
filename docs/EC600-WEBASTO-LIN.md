# Webasto LIN: what the EC600 actually does

Reviewed 29 September 2026. This is a source interpretation, not a Webasto
protocol specification. **B0 is the first payload byte; b0 is its least
significant bit.** The checksum is separate. Every bit in both payloads is
accounted for below, including bits the EC600 ignores.

The main finding is that the EC600 extracts several named status fields, but
most are only stored. **The received LIN-error bit, B7 b7, is the only decoded
Webasto status field found to affect subsequent PSU behaviour:** it controls
the heater LIN-status indication. Receipt of an accepted frame also enables
the next control transmission. Temperature, voltage, heater-error and altitude
calculations all collapse to zero in this source.

## Evidence and scope

Files were read from `P:\03 Design Files\04 Projects\EC600\Flowcode` and copied
to temporary local storage for analysis. Nothing was written to that project.
Line references below are to **EC600PSU V57B.c**, unless explicitly labelled
Flowcode. These references use the same source as the existing LIN catalogue.

| File | SHA-256 |
| --- | --- |
| EC600PSU V57B.c | `911c8716a3a90c0f20f76390018e5f0778ace9b3aec37849fe59d11bd83b7fa2` |
| EC600PSU V57B.fcfx | `69afead30df54d170cded8e08dfb95b3a23431d7111f201f949b6d3224d23ef9` |
| EC600PSU V57C Whale.c | `a47b1059cc2f4867bd46cb3976cacc192def337d91cc58747e218e3384a79228` |
| EC600PSU V57C Whale.fcfx | `c12d90b56f77539bf597fd9b16087d6b8ad90b3a3f6468b04b23fd6aabe578cc` |

The complete generated `FCM_WebastoLIN` function is identical in V57B and V57C
Whale (starting at lines 53817 and 53933 respectively). The parsed `WebastoLIN`
Flowcode macro is also identical between their `.fcfx` files. The suspicious
operators occur in the original V57B Flowcode expressions at lines 27934–28052;
they are not merely a generated-C transcription issue. The V57C project file
is newer than its generated C, so this comparison establishes agreement of
this macro, not agreement of the entire V57C build. Other historical versions
and the installed heater/PSU firmware are outside this review.

## Receive path: heater information, ID 58 / 0x3A / PID 0xBA

The PSU sends the header and requests eight response bytes with enhanced
checksum. In the active main-loop Webasto branch (71129–71192), it accepts the
response only when `RDCount == 8 && RDReturn == 0`, copies all eight bytes into
`Lin58[]`, and sets `WebastoReceived = 1`. The receive wrapper is at 43437.

`FCM_WebastoLIN` runs only for `SetOut3[2] == 5`. On its received-data branch it
performs the following operations (53859–53876). Variable names below omit the
generated C prefix `FCV_`; all these decoded Webasto variables are unsigned
8-bit values. “Stored only” means no consumer beyond declaration and these
assignments was found in the complete V57B generated C.

| Byte | Bits / mask | Source name or label | Exact EC600 operation and result | Subsequent use / remaining uncertainty |
| --- | --- | --- | --- | --- |
| B0 | b4..0 / `0x1F` | `WebastoCMode`, working mode | `B0 & 0x1F`, raw 0–31 | Stored only. No working-mode enumeration. Do not reuse the outgoing requested-mode codes. |
| B0 | b6..5 / `0x60` | No field | Not decoded | Meaning unknown; not evidence that Webasto reserves these bits. |
| B0 | b7 / `0x80` | `WebastoCDiag`, diagnostic flag | `(B0 & 0x80) >> 7`, 0 or 1 | Stored only. No diagnostic request or action follows this flag. Detailed meaning unknown. |
| B1 | b1..0 / `0x03` | `WebastoCErrorStatus` | `B1 & 3`, raw 0–3 | Stored only. No error-state enumeration or fault-code lookup. |
| B1 | b7..2 / `0xFC` | `WebastoCTemperature`, first fragment | Initially `B1 & 0xFC` | Combined with B2 below. Parser comment says actual cabin temperature; declaration says medium temperature. No reliable physical interpretation. |
| B2 | b1..0 / `0x03` | Temperature, second fragment | Final temperature is `(B1 & 0xFC) & (B2 & 3)` = **0** | Stored only. No alignment shift, valid assembly, offset or scale is implemented. |
| B2 | b7..2 / `0xFC` | `WebastoCVoltage`, first fragment | Initially `B2 & 0xFC` | Combined with B3 below. |
| B3 | b1..0 / `0x03` | Voltage, second fragment | Final voltage is `(B2 & 0xFC) & (B3 & 3)` = **0** | Stored only. No valid assembly or conversion to volts. |
| B3 | b7..2 / `0xFC` | No field | Not decoded | Meaning unknown. |
| B4 | b7..0 / `0xFF` | No field | Copied into `Lin58[4]`, not decoded by Webasto routine | Meaning unknown. |
| B5 | b7..0 / `0xFF` | No field | Copied into `Lin58[5]`, not decoded by Webasto routine | Meaning unknown. |
| B6 | b2..0 / `0x07` | No field | Not decoded | Meaning unknown. |
| B6 | b3 / `0x08` | `WebastoCHeaterError` | `(B6 & 8) >> 4` = **0** | Stored only. Mask selects bit 3 but shift removes it. Error meaning/enumeration unknown. |
| B6 | b4 / `0x10` | `WebastoCAltitude` | `(B6 & 16) >> 5` = **0** | Stored only. Mask selects bit 4 but shift removes it. Altitude units or flag semantics unknown. |
| B6 | b7..5 / `0xE0` | No field | Not decoded | Meaning unknown. |
| B7 | b5..0 / `0x3F` | No field | Not decoded | Meaning unknown. |
| B7 | b6 / `0x40` | `WebastoCCompError`, “Comp Error” | `(B7 & 64) >> 6`, 0 or 1 | Stored only. Source does not expand “Comp” or define the error. Earlier reference called this component error; that expansion is unconfirmed. |
| B7 | b7 / `0x80` | `WebastoCLinError`, LIN error | `(B7 & 128) >> 7`, 0 or 1 | Also decoded in the polling branch. 1 clears `LinStatusHeat`; 0 sets it after an accepted response. |

The masks `0xFC` and `0x03` have no common set bits, so their AND is zero for
every input. The two excessive right shifts similarly always yield zero.
Replacing operators or inventing shifts would propose a different protocol;
these tables preserve what the source establishes.

### What the receive data changes

After parsing, the routine clears `WebastoReceived` and sets `WebastoSent = 0`
(53882–53883). It does not send in that same branch. A later call takes the
control branch and may send ID 57. The received mode, diagnostic flag, error
status, temperature, voltage, heater error, altitude and “Comp Error” do not
drive control decisions or CAN payloads in the reviewed C.

In contrast, the polling branch immediately uses B7 b7 (71164–71183):

| Receive outcome | `LinStatusHeat` |
| --- | --- |
| Eight accepted bytes, B7 b7 = 0 | 1 |
| Eight accepted bytes, B7 b7 = 1 | 0 |
| Wrong byte count or nonzero receive return code | 0 |

`FCM_LinStatus` places `LinStatusHeat` in `LinBusStatus` bit 0 (63723–63729).
`LinBusStatus` is sent in **CAN ID 53 (0x035), B6**, for example at 53645 and
53650; `FCM_CAN_Send` at 67649 confirms byte order. Therefore CAN 53 B6 b0 is a
combined receive/link indication, not a full Webasto fault report or heating-on
indicator. The payload LIN-error flag and the receiver's own `RDReturn` are
separate inputs to that indication.

The active Webasto polling case is in the engine-not-running `LinDevice == 0`
slot. Its comment says it needs replicating to other air-heater locations
(71124–71126). The separate `FCM_LIN_Polling` contains similar code, but its
main-loop call is disabled at 69943–69948. The four active Webasto processing
calls (72908, 73006, 73104, 73216) do not establish polling in every slot.

## Transmit path: heater control, ID 57 / 0x39 / PID 0x39

The EC600 sends eight payload bytes with enhanced checksum. Inputs to the
packing calculation are prepared at 53894–53941:

| Input | Power on (`Power == 1`) | Power off |
| --- | --- | --- |
| M: requested mode | `SetOut3[0]` | 0 |
| P: requested power | `SetOut3[1]` | 0 |
| T: target temperature | `HeatingNow` if greater than 4, otherwise 5 | 5 |
| A: target altitude | `0xFF`, comment: signal not available | `0xFF` |
| C: advised cabin temperature | `(IntTempX10 / 10) + 50` | Same calculation |

The source comment enumerates **requested** mode as 0=off, 1=park heating,
2=not used, 3=ventilation, 4=heating boost, 5=heating eco. It describes power
as 0–100 corresponding to 0–100%, and target temperature as 5–35 °C. The routine
enforces only the target's lower bound; it does not clamp target to 35 or power
to 100. These are source-comment meanings, not independently verified device
acceptance ranges. M, P, T, A and C are unsigned bytes. `IntTempX10` is an
unsigned 16-bit value; division is integer division and C is assigned to a
byte. Negative-temperature encoding and out-of-range inputs are not established
by this Webasto calculation.

Packing is at 53965–53982. The distinction between inputs and actual output
matters: the mode and some target bits are destroyed before transmission.

| Byte | Bits | What EC600 actually writes | Meaning / limitation |
| --- | --- | --- | --- |
| B0 | b2..0 | 0 | Initially `M & 7`, then ANDed with target bits occupying b7..5. Requested mode is lost. |
| B0 | b4..3 | 0 | Neither operand supplies these bits. Protocol meaning unknown. |
| B0 | b7..5 | 0 | Initially `(T & 7) << 5` in the temporary; AND with mode produces zero. Target low three bits are lost. **Entire B0 is 0x00.** |
| B1 | b2..0 | T bits 5..3, because A is `0xFF` | Exact expression `((T & 0xF8) >> 3) & (A & 7)`, simplifying to `(T >> 3) & 7`. For intended T=5–35, B1=0–4. |
| B1 | b7..3 | 0 | Target bits 7..6 are cleared by the AND; altitude low bits are not shifted into a separate field. Intended altitude placement cannot be recovered. |
| B2 | b4..0 | 31 (`0x1F`) | `(A & 0xF8) >> 3`; A is fixed `0xFF`. |
| B2 | b5 | 0 | Not supplied by either part of the calculation. |
| B2 | b7..6 | 3 (`0b11`) | Adds `0xC0`. **Entire B2 is 0xDF**, not 0xFF. Protocol meaning of fixed bits unknown. |
| B3 | b7..0 | `0xFF` | Fixed byte; no separate signal meaning given. |
| B4 | b3..0 | `P & 0x0F` | Low four power bits. |
| B4 | b7..4 | `0xF` | Adds `0xF0`; fixed ones. |
| B5 | b2..0 | `(P & 0x70) >> 4` | Power bits 6..4. Recover represented power with `(B4 & 15) OR ((B5 & 7) << 4)`. Source labels intended values 0–100%. |
| B5 | b7..3 | 0 | Entire byte assigned from the masked shift. These bits are known zero in this implementation; protocol meaning unknown. P bit 7 is discarded. |
| B6 | b7..0 | `0xFF` | Fixed byte; no separate signal meaning given. |
| B7 | b7..0 | C | Advised cabin temperature, integer °C + 50 for ordinary in-range values. Example: `IntTempX10=200` gives 70 = `0x46`. No separate bit flags. |

### When control is sent

Startup sets `WebastoSent = 2` (69869), and the CAN 173 settings handler also
sets it to 2 (18427). Processing an accepted status frame sets it to 0. The
control branch sends only when it equals 0, then sets it to 1 (53991–54009).
It records `LinSendResult` but does not check it before setting that flag.
Thus the local comment “only send when a change is detected” is not implemented
as a field comparison: the local `Changed` counter is unused. Even an unchanged
accepted status frame can enable another send. A settings command alone does
not directly satisfy the send condition.

### Worked examples from the executed expressions

These are calculated examples, not captured frames or recommended commands.

| Inputs | Resulting ID 57 payload (hex, B0 first) | Explanation |
| --- | --- | --- |
| Power on; M=1; P=37; T=20; `IntTempX10=200` | `00 02 DF FF F5 02 FF 46` | Mode lost; target reduced to 2; power represents 37%; cabin advice represents 20 °C. |
| Power off; `IntTempX10=200` | `00 00 DF FF F0 00 FF 46` | T=5 and P=0; cabin advice still sent. |

For an accepted ID 58 payload `9F FF FF FF FF FF 18 C0`, the parser stores
mode=31, diagnostic=1, error status=3, temperature=0, voltage=0, heater error=0,
altitude=0, “Comp Error”=1 and LIN error=1. The polling branch clears heater LIN
status. Clearing only B7 b7 (C0 → 40) sets that status despite “Comp Error”
remaining 1: that bit is not used in this decision.

## Verification performed

The extracted V57B C function was compiled and executed locally with LIN
hardware calls stubbed and byte buffers modelled as unsigned 8-bit storage.
65,536 receive cases covered every pair of temperature/voltage fragment bytes
and all flag-byte values. A further 65,536 transmit cases covered every byte
value for requested mode/target, with power swept through all byte values too.
Checks confirmed the zero results, fixed bits, both complete payload examples,
and receive/send flag sequence. This verifies the calculations; it is not a
PSU hardware test or a validation of the manufacturer's intended protocol.

The reference exporter passed the V57B source-hash and 13-ID inventory check.
The full workbook was regenerated, reopened and checked for complete bit
coverage. Temporary source copies and the harness were not added to CanLogger.

## What still cannot be labelled reliably

The source does not supply working-mode or error-status enumerations, a valid
received temperature/voltage conversion, the expansion of “Comp Error”, altitude
semantics, or meanings for the ignored status bits and fixed control fillers.
“Not decoded” does not mean “unused by the heater”. Requested-mode comments do
not define received working modes: the source explicitly says they differ.

Selection 5 is called Webasto here but Whale Ci-Bus in the CAN spreadsheet;
that naming conflict remains. The separate `WebastoPWM` routine does not resolve
these LIN fields. The detailed tables establish what this code does without
claiming which appliance or firmware is physically fitted.

See the [complete LIN reference](EC600-LIN-REFERENCE.md) and
[earlier source findings](EC600-LIN-FINDINGS.md) for the other appliances.
