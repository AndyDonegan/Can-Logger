#!/usr/bin/env python3
"""Summarize a receiver trace without opening any device or inferring missing frames."""
import argparse
from collections import Counter
from pathlib import Path


def event_counts(payload):
    counts = Counter()
    i = 0
    sizes = {0x10: 2, 0x12: 2, 0x13: 3, 0x14: 1, 0x15: 3,
             0x16: 1, 0x17: 1, 0x18: 1, 0x19: 1, 0x1a: 2, 0x1b: 1, 0x1c: 2,
             0x80: 2, 0x81: 2, 0x82: 2, 0x83: 1, 0x84: 1, 0x85: 3, 0x86: 2, 0x87: 2}
    while i < len(payload):
        tag = payload[i]
        size = 2 + payload[i+1] if tag == 0x11 and i+1 < len(payload) else sizes.get(tag)
        if size is None or i + size > len(payload):
            raise ValueError(f'Unsupported or truncated event at offset {i}: 0x{tag:02X}')
        counts[tag] += 1
        i += size
    return counts


def summarize(path):
    packets = Counter()
    payload = bytearray()
    times = {}
    frames = []
    settings = []
    with path.open(encoding='utf-8-sig') as source:
        for line in source:
            if 'RAW_USB\t' in line:
                fields = line[line.index('RAW_USB\t'):].strip().split('\t')
                if len(fields) != 5 or fields[2] != '__rawRead':
                    continue
                data = bytes.fromhex(fields[4])
                if not data:
                    continue
                if data[0] == 0x86:
                    if len(data) < 2 or data[1] > len(data) - 2:
                        raise ValueError('Invalid event-buffer length')
                    payload.extend(data[2:2+data[1]])
                packets[data[0]] += 1
                times.setdefault(data[0], []).append(int(fields[1]))
            elif 'FRAME\t' in line:
                fields = line[line.index('FRAME\t'):].rstrip('\r\n').split('\t')
                if len(fields) == 7:
                    frames.append(fields)
            elif 'CONFIG_BAUD\t' in line:
                settings.append(line.split('CONFIG_BAUD\t', 1)[1].strip())
    print('Adapter rate readback:', ', '.join(settings) or 'not recorded')
    print('Decoded library records:', len(frames))
    for prefix, count in sorted(packets.items()):
        observed = times[prefix]
        print(f'USB input prefix 0x{prefix:02X}: {count} reports over {(observed[-1]-observed[0])/1000:.3f}s')
    if frames:
        last = int(frames[-1][1])
        # 0x86 is the leading event-buffer packet tag in the inspected Microchip parser.
        later = sum(t > last for t in times.get(0x86, []))
        print('Event-buffer reports after last decoded record:', later)
        for frame in frames[:20]:
            print(f'PID 0x{int(frame[2]):02X}, reported baud {frame[3]}, vendor error {frame[5]}, raw {frame[6] or "(empty)"}')
    if payload:
        counts = event_counts(payload)
        for tag, name in [(0x10, 'data-byte events'), (0x83, 'received-break events'), (0x85, 'auto-baud events')]:
            print(f'{name}: {counts[tag]}')
    print('Counts classify report prefixes, not valid LIN frames. Trailing HID buffer bytes may be stale.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace', type=Path)
    summarize(parser.parse_args().trace)
