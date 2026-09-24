#!/usr/bin/env python3
"""Export the reviewed local catalogue; optional read-only generated-C inventory audit."""
import argparse
import csv
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, help='Optional generated C file to read and audit; never modified')
    args = parser.parse_args()
    catalogue = json.loads((ROOT / 'lin/ec600-lin-scheme.json').read_text())
    entries = catalogue['entries']
    if args.source:
        content = args.source.read_bytes()
        assert hashlib.sha256(content).hexdigest() == catalogue['sha256'], 'Source hash differs; review before refreshing definitions'
        source = content.decode('utf-8', errors='replace')
        calls = re.findall(r'(?:FCD_\w+_LinMaster1__SendMessage(?:WithData)?|FCM_SendIDAndRxData(?:Classic)?)\(([^,\n]+),', source)
        ids = {int(a, 0) for a in calls if re.fullmatch(r'(?:0x[\da-fA-F]+|\d+)', a.strip())}
        assert ids == {e['id'] for e in entries}, f'ID inventory differs: {ids}'
        print(f'Read-only source audit passed: {len(ids)} distinct IDs; matching SHA-256.')
    def pid(i):
        b = lambda n: (i >> n) & 1
        return i | ((b(0)^b(1)^b(2)^b(4)) << 6) | ((1^b(1)^b(3)^b(4)^b(5)) << 7)
    def esc(value):
        return str(value).replace('|', r'\|').replace('\n', '<br>')
    lines = ['# EC600 LIN ID and byte reference', '',
        f"Source: `{catalogue['source']}`. SHA-256: `{catalogue['sha256']}`.", '',
        'Read-only review of the matching V57B generated C and Flowcode project. This is a dictionary of what the PSU source sends or interprets, not a complete manufacturer specification or firmware identification.', '',
        'All 13 distinct literal IDs in the LIN send/receive call sites are included. Parameterised calls are the two receive wrappers. Historical firmware variants were not exhaustively catalogued. Disabled examples are labelled; runtime appliance selection determines which IDs actually appear.', '',
        catalogue['byteNumbering'], '',
        'The PSU sends every header. “Appliance → PSU” describes who supplies the response data. Every transaction listed requests/sends eight payload bytes; the checksum is additional.', '',
        'In the app: EC600 LIN reference beside Start LIN. Select an ID and layout. Live rows show descriptions; hover displays binary payload bytes and source fields. Hover details use automatic heater identification; choose an appliance in the reference window to inspect a specific make. Double-click a received row for a frozen, scrollable inspection. Hover details are height-limited and scrollable: move into the popup to read the complete definition. The displayed frame stays frozen while open.', '',
        'Shared IDs alone never identify a make. Auto heater uses confirmed-format CAN 133 B2 settings (2=Truma CP+, 3=Whale, 4=Eberspacher), even if ID 133 is hidden by the watch list. Settings describe configuration, not physical presence. Each LIN row retains the selection available at capture/display time; CAN stop/start clears it. Alde generation and setting 5 remain manual: the firmware calls 5 Webasto while the CAN spreadsheet calls it Whale Ci-Bus. No payload-based guess or configuration command is sent. Incomplete frames retain raw bytes and are not filled with zeros. A reference field description does not assert that a captured value is valid; PID, checksum and receive status remain visible.', '',
        '| ID dec | ID hex | Expected PID | Description | Response data direction | Bytes | Checksum |',
        '| --- | --- | --- | --- | --- | --- | --- |']
    with (ROOT / 'docs/EC600-LIN-IDS.csv').open('w', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(['ID decimal','ID hex','Expected PID','Description','Data direction','Data bytes','Checksum','Layouts'])
        for e in entries:
            row = [e['id'],f"0x{e['id']:02X}",f"0x{pid(e['id']):02X}",e['name'],e['direction'],e['length'],e['checksum']]
            writer.writerow(row + ['; '.join(l['name'] for l in e['layouts'])])
            lines.append('| ' + ' | '.join(map(esc, row)) + ' |')
    for e in entries:
        lines += ['', f"## ID {e['id']} / 0x{e['id']:02X} — {e['name']}", '', e['notes']]
        for layout in e['layouts']:
            lines += ['', '### ' + layout['name'], '', f"Source routines / generated-C lines: {layout['source']}.", '', '| Byte | Meaning from source |', '| --- | --- |']
            lines += [f'| B{i} | {esc(value)} |' for i,value in enumerate(layout['bytes'])]
            if layout['notes']: lines += ['', layout['notes']]
    lines += ['', '## Source limitations', '',
        'The active main loop supplies the appliance schedule. The separate FCM_LIN_Polling call is disabled. AL-KO/LEVC experimental code and obsolete clock-setting branches are distinguished from active paths.', '',
        'Truma aircon information comments disagree with executable offsets. Webasto contains suspect AND/shift operations. FreshJet response checking compares an 8-bit byte with 0x1F2. These are documented as source inconsistencies, not silently corrected into an assumed protocol.', '',
        'No LIN transmissions, firmware changes or P-drive writes were performed for this feature. Full proprietary firmware copies are not included in the repository.', '',
        'Regenerate this document and CSV with `python3 scripts/export-lin-reference.py`; optionally add `--source <generated-C-path>` for a read-only hash and complete literal-ID inventory check.', '']
    (ROOT / 'docs/EC600-LIN-REFERENCE.md').write_text('\n'.join(lines))
    print(f"Exported {len(entries)} IDs and {sum(len(e['layouts']) for e in entries)} layouts.")

if __name__ == '__main__':
    main()
