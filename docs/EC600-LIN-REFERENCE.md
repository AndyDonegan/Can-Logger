# EC600 LIN ID and byte reference

Source: `EC600PSU V57B.c`. SHA-256: `911c8716a3a90c0f20f76390018e5f0778ace9b3aec37849fe59d11bd83b7fa2`.

Read-only review of the matching V57B generated C and Flowcode project. This is a dictionary of what the PSU source sends or interprets, not a complete manufacturer specification or firmware identification.

All 13 distinct literal IDs in the LIN send/receive call sites are included. Parameterised calls are the two receive wrappers. Historical firmware variants were not exhaustively catalogued. Disabled examples are labelled; runtime appliance selection determines which IDs actually appear.

B0 is the first payload byte; b0 is its least significant bit. Checksum is separate.

The PSU sends every header. “Appliance → PSU” describes who supplies the response data. Every transaction listed requests/sends eight payload bytes; the checksum is additional.

In the app: LIN data → EC600 LIN reference. Select an ID and layout. Live rows show descriptions; hover displays binary payload bytes and source fields. Choose Hover layout for a specific make. Double-click a received row for a frozen, scrollable inspection. Long hover previews explicitly direct you to the full details rather than extending beyond the screen.

Shared IDs alone never identify a make. Auto heater uses confirmed-format CAN 133 B2 settings (2=Truma CP+, 3=Whale, 4=Eberspacher), even if ID 133 is hidden by the watch list. Settings describe configuration, not physical presence. Each LIN row retains the selection available at capture/display time; CAN stop/start clears it. Alde generation and setting 5 remain manual: the firmware calls 5 Webasto while the CAN spreadsheet calls it Whale Ci-Bus. No payload-based guess or configuration command is sent. Incomplete frames retain raw bytes and are not filled with zeros. A reference field description does not assert that a captured value is valid; PID, checksum and receive status remain visible.

| ID dec | ID hex | Expected PID | Description | Response data direction | Bytes | Checksum |
| --- | --- | --- | --- | --- | --- | --- |
| 8 | 0x08 | 0x08 | Air-conditioning control | PSU → appliance | 8 | Enhanced |
| 11 | 0x0B | 0x8B | Dometic refrigerator control / echo | PSU → appliance | 8 | Enhanced |
| 12 | 0x0C | 0x4C | Dometic refrigerator information | Appliance → PSU | 8 | Enhanced |
| 23 | 0x17 | 0x97 | Air-conditioning information | Appliance → PSU | 8 | Enhanced |
| 26 | 0x1A | 0x1A | Alde heating / hot-water control | PSU → appliance | 8 | Enhanced |
| 27 | 0x1B | 0x5B | Alde heating / hot-water information | Appliance → PSU | 8 | Enhanced |
| 36 | 0x24 | 0x64 | AL-KO ATC information | Appliance → PSU | 8 | Enhanced |
| 55 | 0x37 | 0x37 | Water-heater control — Truma / Whale | PSU → appliance | 8 | Enhanced |
| 56 | 0x38 | 0x78 | Water-heater information — Truma / Whale | Appliance → PSU | 8 | Enhanced |
| 57 | 0x39 | 0x39 | Space-heater control — multiple makes | PSU → appliance | 8 | Enhanced |
| 58 | 0x3A | 0xBA | Space-heater information — multiple makes | Appliance → PSU | 8 | Enhanced |
| 60 | 0x3C | 0x3C | Diagnostic / configuration request | PSU → appliance | 8 | Classic |
| 61 | 0x3D | 0x7D | Diagnostic response | Appliance → PSU | 8 | Classic |

## ID 8 / 0x08 — Air-conditioning control

Shared by Truma and Dometic; select the fitted appliance layout.

### Truma aircon

Source routines / generated-C lines: FCM_TAircon (41635); FCM_AirconCTRL (35973).

| Byte | Meaning from source |
| --- | --- |
| B0 | Temperature low byte; little-endian value with next byte: (raw - 2730) / 10 degrees C. |
| B1 | Temperature high byte. See B0. |
| B2 | Fan code: 0x71=low, 0x72=medium, 0x73=high, 0x74=night. |
| B3 | b3..0=operating mode: 0=off, 4=fan, 5=cooling, 6=heating, 7=automatic. |
| B4 | AC power low byte (source comment; not assigned a unit here). |
| B5 | AC power high byte (source comment). |
| B6 | Light level, 0..100%; source maps dimmer target / 2. |
| B7 | RFU according to source comment. |

The later Dometic-style section inside TAircon is disabled; its packed format is not a Truma layout.

### Dometic aircon

Source routines / generated-C lines: FCM_DAircon (45004); FCM_AirconCTRL (35973).

| Byte | Meaning from source |
| --- | --- |
| B0 | b7=aircon on; b6=light on; b1..0=part of operating-mode code; b2=fan auto flag in control path. |
| B1 | b7..4=target temperature minus 16 degrees C; b3..2=fan setting; b1..0=other operating-mode bits. |
| B2 | Copied from info frame when synchronising; meaning not established. |
| B3 | Copied from info frame when synchronising; meaning not established. |
| B4 | b7..4=iFeel temperature minus 16 degrees C; low nibble preserved. |
| B5 | Light dimming code written as 0, 24, 56, 88, 120 or 152. |
| B6 | Copied from info frame; meaning not established. |
| B7 | b2=sync frame (0x04), cleared after synchronising. |

## ID 11 / 0x0B — Dometic refrigerator control / echo

FridgeCTRL echoes ID 12 when no change is pending; that echo has the ID 12 status layout. Thetford branch is a placeholder, not a verified protocol.

### Dometic fridge

Source routines / generated-C lines: FCM_Fridge6 (32313); FCM_FridgeCTRL (49420).

| Byte | Meaning from source |
| --- | --- |
| B0 | b2..0=mode: 0=off, 1=auto, 3=gas, 5=12 V, 7=230 V. b0 acts as on flag. |
| B1 | Cooling setting = SetOut4[1] + 1. |
| B2 | Frame-heater setting from SetOut4[2] for large fridge/freezer; otherwise 0. |
| B3 | Written as 0 in control frame; old hour-setting code disabled. In echo: error byte. |
| B4 | Written as 0 in control frame; old minute-setting code disabled. In echo: status flags (see ID 12). |
| B5 | b4=automatic mode available; b2=frame heater fitted. Written as 0x10 or 0x14. |
| B6 | Written as 0 (spare) in control frame; echoed otherwise. |
| B7 | Written as 0 (spare) in control frame; echoed otherwise. |

## ID 12 / 0x0C — Dometic refrigerator information



### Dometic fridge

Source routines / generated-C lines: FCM_FridgeINFO (32762); FCM_Fridge6 (32313).

| Byte | Meaning from source |
| --- | --- |
| B0 | b2..0=mode: 0=off, 1=auto, 3=gas, 5=12 V, 7=230 V; PSU masks other bits. b0=on. |
| B1 | Cooling level; PSU subtracts 1 for SetOut4[1]. Zero is treated as fridge starting up. |
| B2 | Frame-heater setting copied to SetOut4[2]. |
| B3 | Fridge error code; PSU ignores errors during first 15 seconds after on. |
| B4 | b2..0=FridgeState; b3=manual mode; b4=door open. Remaining bits not decoded. |
| B5 | Stored and echoed; no response-field meaning established. |
| B6 | Stored and echoed; no response-field meaning established. |
| B7 | Stored and echoed; no response-field meaning established. |

## ID 23 / 0x17 — Air-conditioning information

Truma and Dometic layouts differ. Truma comments conflict with executed offsets; mappings below follow executed code.

### Truma aircon

Source routines / generated-C lines: FCM_AirconINFO (66897); FCM_TAircon (41635).

| Byte | Meaning from source |
| --- | --- |
| B0 | Not decoded by the active Truma information path. |
| B1 | Not decoded by the active Truma information path. |
| B2 | Temperature low byte; little-endian value with next byte: (raw - 2730) / 10 degrees C. |
| B3 | Target temperature high byte. See B2. |
| B4 | Fan code; 0x71..0x74 identifies Truma in this PSU branch. |
| B5 | b3..0=operating mode; PSU masks upper nibble. 0=off, 4=fan, 5=cool, 6=heat, 7=auto. |
| B6 | AC power low byte, stored as TACPowerL; scale not established. |
| B7 | AC power high byte, stored as TACPowerH; scale not established. |

The nearby comment describes target at B0/B1 and light at B6; executable code uses target B2/B3 and power B6/B7. Do not apply that comment as a verified layout.

### Dometic aircon

Source routines / generated-C lines: FCM_DAircon (45004); FCM_AirconINFO (66897).

| Byte | Meaning from source |
| --- | --- |
| B0 | b7=on / mode component; b6=light; b1..0=mode component. |
| B1 | b7..4=target temperature minus 16 degrees C; b3..2=fan setting; b1..0=mode component. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | b7..4=iFeel temperature minus 16 degrees C. |
| B5 | Light data echoed to control; dimming codes are set in ID 8. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | b0 indicates remote/settings change in the source; other flags not established. |

Mode is assembled as ((B0 & 128) >> 3) | (B1 & 3) | ((B0 & 3) << 2); do not interpret a single mode bit alone.

## ID 26 / 0x1A — Alde heating / hot-water control

Alde 3020+ (CPSerial > 30) and older EC645 interface layouts differ.

### Alde 3020+

Source routines / generated-C lines: FCM_Alde9 (55024); FCM_AldeINFOCTRL9 (43527).

| Byte | Meaning from source |
| --- | --- |
| B0 | Sent as 0. |
| B1 | Sent as 0. |
| B2 | Sent as 0. |
| B3 | b5..0=(target degrees C - 5) * 2, or 0 when off; b6=gas enabled. b7 not set by this routine. |
| B4 | b7..6=electric selection 0..3; code can add (HeatingNow - 5) * 2 in low bits. |
| B5 | b0=Alde on; b4..3=hot-water selection: 0=off, 1=normal, 2=boost; other bits not set here. |
| B6 | Sent as 0. |
| B7 | Sent as 0. |

The sender explicitly transmits only Lin26[3..5], zeroing other bytes; not the old EC645 layout.

### Alde / EC645 legacy

Source routines / generated-C lines: FCM_Alde2 (63840); FCM_AldeINFOCTRL (63574).

| Byte | Meaning from source |
| --- | --- |
| B0 | b0=heater on; b1=gas; b3..2=electric kW 0..3; b5..4=water setting: 0=off, 2=normal, 3=boost (returns to 2 after timer). b7..6 not established. |
| B1 | Room target low byte; little-endian B1/B2 = target degrees C * 10. |
| B2 | Room target high byte. See B1. |
| B3 | Normal control sends constant 64 (0x40). Other branches send time/date data; see notes. |
| B4 | Normal control sends 0. |
| B5 | Normal control sends 0. |
| B6 | Normal control sends 0. |
| B7 | Normal control sends 0. |

Alde2 also sends clock-setting frames on ID 26. Those must not be decoded as ordinary control; see full source references.

### Alde / EC645 clock

Source routines / generated-C lines: FCM_Alde2 (64972-64997).

| Byte | Meaning from source |
| --- | --- |
| B0 | 0 |
| B1 | 0 |
| B2 | 0 |
| B3 | b7=clock marker; lower bits = Day - 1 (source Day convention). |
| B4 | Hour. |
| B5 | Minute. |
| B6 | 0 |
| B7 | 0 |

Clock frame uses B3 bit7, unlike normal control B3=0x40. Sent twice by the source.

## ID 27 / 0x1B — Alde heating / hot-water information



### Alde 3020+

Source routines / generated-C lines: FCM_AldeINFOCTRL9 (43527).

| Byte | Meaning from source |
| --- | --- |
| B0 | Room temperature = (B0 - 84) / 2 degrees C (integer arithmetic in PSU). |
| B1 | Bed temperature according to comment; not used, scale not established. |
| B2 | Outdoor temperature according to comment; not used, scale not established. |
| B3 | b5..0=temperature setting, degrees C = (field + 10) / 2; b6=gas; b7=energy priority. |
| B4 | b7..6=electric selection 0..3; other bits not decoded here. |
| B5 | b0=heater state; b1=manual mode; b2=error flag; b4..3=water selection; b7=pump. b6..5 not decoded. |
| B6 | Stored only; no meaning established. |
| B7 | Stored only in this layout; do not apply legacy CRC-enable flag. |

### Alde / EC645 legacy

Source routines / generated-C lines: FCM_Alde2 (63840); FCM_AldeINFOCTRL (63574).

| Byte | Meaning from source |
| --- | --- |
| B0 | Settings compared to ID 26 B0 (heater/gas/electric/water); other meanings not established. |
| B1 | Target temperature low byte compared to ID 26 B1. |
| B2 | Target temperature high byte compared to ID 26 B2. |
| B3 | HeatingStatus copied from this byte; detailed flags not defined here. |
| B4 | HeatingError, accepted only when <64. |
| B5 | Not defined by the reviewed PSU code. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | Value 1 means EC645 CRC checking enabled according to PSU code. Not the LIN checksum byte. |

Legacy receiver may accept data without its usual CRC check when B7 != 1. The analyzer still reports its own checksum result.

## ID 36 / 0x24 — AL-KO ATC information

Active scheduler polls ID 36 for ATC. Alko2 contains a disabled LEVC experiment; those meanings are not active ATC fields.

### AL-KO ATC

Source routines / generated-C lines: FCM_Alko1 (44749); main polling (70701, 71919).

| Byte | Meaning from source |
| --- | --- |
| B0 | b2..0=ATCStatus; b4..3=ATCHistogram; b7..5=ATCACCStatus. Enumeration meanings not supplied. |
| B1 | Not defined by the reviewed PSU code. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Not defined by the reviewed PSU code. |
| B5 | Not defined by the reviewed PSU code. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | Not defined by the reviewed PSU code. |

Source says the split variables are not currently used; raw bytes are still forwarded in CAN ID 216.

### LEVC experiment (disabled)

Source routines / generated-C lines: FCM_Alko2 (44394).

| Byte | Meaning from source |
| --- | --- |
| B0 | LEVC_Grid in disabled code. |
| B1 | LEVC_DCDC in disabled code. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Not defined by the reviewed PSU code. |
| B5 | Not defined by the reviewed PSU code. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | LEVC_Brake in disabled code. |

Entire LIN receive/interpretation block is #if 0. Historical experiment only, not evidence of current bus semantics.

## ID 55 / 0x37 — Water-heater control — Truma / Whale

Shared ID: choose the installed heater layout. Valid checksum does not identify the make.

### Truma CP+

Source routines / generated-C lines: FCM_TrumaSetHW (21787); FCM_Truma3A (46325).

| Byte | Meaning from source |
| --- | --- |
| B0 | Water target low byte: 00 00=off; 3A 0C=40 C Eco; 02 0D=60 C Hot (B0 then B1). |
| B1 | Water target high byte. See B0. |
| B2 | Energy code used by TrumaEnergy; 1=gas, 2=electric, 3=mixed (subject to PSU limiting). |
| B3 | Electric power low byte; B3/B4: 84 03=900 W; 08 07=1800 W. |
| B4 | Electric power high byte. See B3. |
| B5 | Sent as 0. |
| B6 | Sent as 0. |
| B7 | Sent as 0. |

### Whale

Source routines / generated-C lines: FCM_Whale5 (58677); FCM_WhaleSetWH (38174).

| Byte | Meaning from source |
| --- | --- |
| B0 | Temperature low byte; little-endian value with next byte: (raw - 2730) / 10 degrees C. Off setting encodes 0 C (0x0AAA), not all-zero bytes. |
| B1 | Water target high byte. Modes: off=0 C, frost=25 C, Eco=55 C, Max=72 C. |
| B2 | Energy selection: 0=off, 1=electric stage 1, 3=stage 2, 7=stage 3, 16=gas, 17/19/23=mixed gas + electric. b4=gas; b2..0=electric stage code (not independent switches). |
| B3 | Lin55[3] forwarded; meaning not established in Whale path. |
| B4 | Lin55[4] forwarded; meaning not established in Whale path. |
| B5 | Lin55[5] forwarded; meaning not established. |
| B6 | Lin55[6] forwarded; meaning not established. |
| B7 | Lin55[7] forwarded; meaning not established. |

## ID 56 / 0x38 — Water-heater information — Truma / Whale

A header without response bytes can be a poll of an absent heater; it is not an eight-byte payload of zeros.

### Truma CP+

Source routines / generated-C lines: FCM_Truma3A (46325); main polling (70307).

| Byte | Meaning from source |
| --- | --- |
| B0 | Water target low byte, compared to ID 55 B0: 00 00=off; 3A 0C=Eco; 02 0D=Hot. |
| B1 | Water target high byte. |
| B2 | Energy code used by TrumaEnergy; 1=gas, 2=electric, 3=mixed (subject to PSU limiting). |
| B3 | Electric power low byte; B3/B4 tested for 900 W / 1800 W. |
| B4 | Electric power high byte. |
| B5 | b7=heater error flag; triggers Truma diagnostic request on ID 60. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | Not defined by the reviewed PSU code. |

### Whale

Source routines / generated-C lines: FCM_Whale5 (58677); main polling (70387).

| Byte | Meaning from source |
| --- | --- |
| B0 | Not defined by the reviewed PSU code. |
| B1 | Not defined by the reviewed PSU code. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Supply voltage: source tests >90 as >9 V, implying tenths of a volt. |
| B5 | b3..0=HotWaterError (updated 2026); upper nibble not decoded. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | HotWaterStatus; b6=Whale panel busy/manual interaction. |

## ID 57 / 0x39 — Space-heater control — multiple makes

Shared by Truma, Whale, Eberspacher and Webasto. No automatic make detection.

### Truma CP+

Source routines / generated-C lines: FCM_TrumaSetHeating (41597); FCM_Truma3A (46325).

| Byte | Meaning from source |
| --- | --- |
| B0 | Target temperature low byte; B0 + ((B1 & 15) << 8): (raw - 2730) / 10 C; zero=off. |
| B1 | b3..0=target high bits; b7..4=TrumaMode retained from ID 58. |
| B2 | Sent as 0. |
| B3 | Energy code used by TrumaEnergy; 1=gas, 2=electric, 3=mixed (subject to PSU limiting). |
| B4 | Sent as 0. |
| B5 | Sent as 0. |
| B6 | Sent as 0. |
| B7 | Sent as 0. |

### Whale

Source routines / generated-C lines: FCM_Whale5 (58677).

| Byte | Meaning from source |
| --- | --- |
| B0 | Temperature low byte; little-endian value with next byte: (raw - 2730) / 10 degrees C. |
| B1 | Target high byte. See B0. |
| B2 | Energy selection: 0=off, 1=electric stage 1, 3=stage 2, 7=stage 3, 16=gas, 17/19/23=mixed gas + electric. b4=gas; b2..0=electric stage code (not independent switches). |
| B3 | Feature code: 0=normal, 4=frost (5 C), 2=night (16 C), 1=fan only (0 C). b2=frost, b1=night, b0=fan-only in emitted codes. |
| B4 | Forwarded Lin57[4]; meaning not established. |
| B5 | Forwarded Lin57[5]; meaning not established. |
| B6 | Room temperature low byte from IntTL; B6/B7 little-endian: (raw - 2730) / 10 C (temperature conversion in source). |
| B7 | Room temperature high byte from IntTH. |

### Eberspacher

Source routines / generated-C lines: FCM_EberspacherLIN (36595).

| Byte | Meaning from source |
| --- | --- |
| B0 | Fixed 0xFF override. Disabled altitude calculation must not be treated as active. |
| B1 | Command mode: 0=off, 1=on TT, 2=on TTCT/TTCC (source naming differs), 8=ventilate. Purge is status only. |
| B2 | Target temperature = (B2 - 40) / 2 C; source writes 40 for zero C. |
| B3 | Room temperature low byte: raw = IntTempByte * 10 + 500. |
| B4 | Room temperature high byte. See B3; IntTempByte scale not assumed here. |
| B5 | Written as 0 when power on. |
| B6 | Written as 0 when power on. |
| B7 | Written as 0 when power on. |

### Webasto

Source routines / generated-C lines: FCM_WebastoLIN (53817).

| Byte | Meaning from source |
| --- | --- |
| B0 | Source attempts mode b2..0 and target low bits b7..5, but combines disjoint masks using AND: produces zero. No corrected decode assumed. |
| B1 | Source ANDs target high bits with altitude low bits; intended packing is uncertain. |
| B2 | Altitude upper part (target altitude >> 3), plus 0xC0. Target altitude fixed 0xFF here. |
| B3 | Fixed 0xFF. |
| B4 | b3..0=power low nibble; b7..4 fixed 0xF. |
| B5 | b2..0=power bits 6..4. Combined power = (B4 & 15) \| ((B5 & 7) << 4). |
| B6 | Fixed 0xFF. |
| B7 | Advised cabin temperature: degrees C + 50. |

Source contains suspect AND packing. These notes describe actual code and are not a corrected manufacturer protocol.

## ID 58 / 0x3A — Space-heater information — multiple makes

Select the fitted heater; do not interpret all alternative layouts as simultaneous signals.

### Truma CP+

Source routines / generated-C lines: FCM_Truma3A (46325); main polling (70006).

| Byte | Meaning from source |
| --- | --- |
| B0 | Target low byte; raw=B0+((B1 & 15)<<8); (raw-2730)/10 C; zero=off. |
| B1 | b3..0=target high bits; b7..4=TrumaMode, extracted and removed before PSU temperature arithmetic. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Energy setting compared with TrumaEnergy (1=gas, 2=electric, 3=mixed). |
| B5 | Not defined by the reviewed PSU code. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | Not defined by the reviewed PSU code. |

Only fields actually read by this PSU path are named; other bytes are retained raw.

### Whale

Source routines / generated-C lines: FCM_Whale5 (58677); main polling (70052).

| Byte | Meaning from source |
| --- | --- |
| B0 | Not defined by the reviewed PSU code. |
| B1 | Not defined by the reviewed PSU code. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Supply voltage: source tests >90 as >9 V, implying 0.1 V per count. |
| B5 | b3..0=HeatingError (updated 2026); upper nibble not decoded. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | HeatingStatus; b6=Whale panel busy/manual interaction. |

### Eberspacher

Source routines / generated-C lines: FCM_EberspacherLIN (36595); main Eberspacher polling branches.

| Byte | Meaning from source |
| --- | --- |
| B0 | Not defined by the reviewed PSU code. |
| B1 | Heater error code, copied to HeatingError. |
| B2 | Not defined by the reviewed PSU code. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Target temperature = (B4 - 40) / 2 C. |
| B5 | Not defined by the reviewed PSU code. |
| B6 | b7..4=heater mode: 0=off, 1=on TT, 2=ventilate, 4=purge, 5=on TTCT (source labels). |
| B7 | b7=EbLinStatus; remaining bits not decoded here. |

### Webasto

Source routines / generated-C lines: FCM_WebastoLIN (53817).

| Byte | Meaning from source |
| --- | --- |
| B0 | b4..0=working mode; b7=diagnostic flag. |
| B1 | b1..0=error status; b7..2 used as temperature fragment. |
| B2 | b1..0=second temperature fragment; b7..2=voltage fragment. |
| B3 | b1..0=second voltage fragment. |
| B4 | Not defined by the reviewed PSU code. |
| B5 | Not defined by the reviewed PSU code. |
| B6 | b3 mask named heater error; b4 mask named altitude. Source then shifts by 4/5, yielding zero: do not copy those shifts as valid decoding. |
| B7 | b6=component error; b7=LIN error. |

Temperature and voltage fragments are combined with disjoint AND operations in this source, giving zero. Physical scaling cannot be established from these calculations.

## ID 60 / 0x3C — Diagnostic / configuration request

Shared diagnostic ID; meaning depends on addressed device and transaction. Includes normal diagnostics and configuration/test routines; not a transmit template.

### Diagnostic / configuration

Source routines / generated-C lines: FCM_TrumaDiag (44244); FCM_AldeDiag (20871); FCM_FreshjetNAD (21534); FCM_AldeRCP (12459).

| Byte | Meaning from source |
| --- | --- |
| B0 | Address/NAD: examples 0x02 Truma, 0x10 Alde or old FreshJet, 0x99 reassigned FreshJet. Address alone can be ambiguous. |
| B1 | PCI / length control. Examples 0x06 single-frame, 0x02 Alde RCP request; multi-frame variants differ. |
| B2 | Service in single-frame requests: 0xB2 read identifier/error example; 0xB0 FreshJet address change; 0xA0 Alde RCP request. First-frame layout can place length here. |
| B3 | Request-specific data. Alde RCP example 0x01; Truma diagnostic example 0x23. |
| B4 | Request-specific data; not a universal flag byte. |
| B5 | Request-specific data; not a universal flag byte. |
| B6 | Request-specific data; not a universal flag byte. |
| B7 | Request-specific data. FreshJet address-change example: new NAD 0x99 or 0x10. |

Observed source examples: Truma 02 06 B2 23 17 46 40 13; Alde RCP 10 02 A0 01 FF FF FF FF. Alde time/date uses multi-frame diagnostic messages; session reassembly is not implemented.

## ID 61 / 0x3D — Diagnostic response

Interpret in context of the ID 60 request and device address; no automatic diagnostic-session reconstruction.

### Truma diagnostic

Source routines / generated-C lines: FCM_TrumaDiag (44244).

| Byte | Meaning from source |
| --- | --- |
| B0 | Response addressing / transaction data; code does not validate a universal field layout here. |
| B1 | Response transport data; not application temperature. |
| B2 | Response service/data; not assigned universally. |
| B3 | Response data; no local meaning established. |
| B4 | Truma error class (source comment). |
| B5 | Truma error code. If 255, retained directly; else PSU packs error = B5 + (B4 << 5). |
| B6 | Not defined by the reviewed PSU code. |
| B7 | Not defined by the reviewed PSU code. |

This mapping applies only to a Truma diagnostic response, not every ID 61 frame.

### Alde diagnostic / RCP

Source routines / generated-C lines: FCM_AldeDiag (20871); FCM_AldeRCP (12459).

| Byte | Meaning from source |
| --- | --- |
| B0 | Device addressing (NAD). |
| B1 | Transport control / frame sequence. Multi-frame transaction context required. |
| B2 | First-frame length or subsequent RCP data depending on frame type. |
| B3 | First-frame service / subsequent RCP data depending on frame type. |
| B4 | Transaction-specific data. |
| B5 | First-frame RCP payload starts here in reviewed assembly code. |
| B6 | RCP payload. |
| B7 | RCP payload. |

PSU collects B5..B7 from first frame and B2..B7 from following frames into RCPString, then interprets tagged elements. No single-frame bit dictionary is justified.

### FreshJet diagnostic

Source routines / generated-C lines: FCM_FreshjetNAD (21534).

| Byte | Meaning from source |
| --- | --- |
| B0 | Address compared with 0x99 or 0x10. |
| B1 | Compared with 0x06. |
| B2 | Source compares an 8-bit value with 0x1F2 (outside byte range); this check is suspect, not a valid expected byte. |
| B3 | Not defined by the reviewed PSU code. |
| B4 | Not defined by the reviewed PSU code. |
| B5 | Not defined by the reviewed PSU code. |
| B6 | Not defined by the reviewed PSU code. |
| B7 | Not defined by the reviewed PSU code. |

Configuration/testing path; exact manufacturer response layout not established.

## Source limitations

The active main loop supplies the appliance schedule. The separate FCM_LIN_Polling call is disabled. AL-KO/LEVC experimental code and obsolete clock-setting branches are distinguished from active paths.

Truma aircon information comments disagree with executable offsets. Webasto contains suspect AND/shift operations. FreshJet response checking compares an 8-bit byte with 0x1F2. These are documented as source inconsistencies, not silently corrected into an assumed protocol.

No LIN transmissions, firmware changes or P-drive writes were performed for this feature. Full proprietary firmware copies are not included in the repository.

Regenerate this document and CSV with `python3 scripts/export-lin-reference.py`; optionally add `--source <generated-C-path>` for a read-only hash and complete literal-ID inventory check.
