"""Summarize preserved probe sessions offline; never access hardware or infer current/SOC."""
import argparse
import hashlib
import json
from pathlib import Path


def analyze(session):
    summary = json.loads((session / 'summary.json').read_text(encoding='utf-8-sig'))
    expected = json.loads((session / 'hashes.json').read_text(encoding='utf-8-sig'))
    mismatches = [item['file'] for item in expected
                  if hashlib.sha256((session / item['file']).read_bytes()).hexdigest().upper()
                  != item['sha256'].upper()]
    readings = []
    second_words = []
    invalid = []
    for path in sorted(session.glob('battery-voltage-*.json')):
        record = json.loads(path.read_text(encoding='utf-8-sig'))
        value = record.get('millivolts')
        attempts = record.get('attempts', [])
        wire = bytes.fromhex(attempts[-1]['hex']) if attempts else b''
        valid = (record.get('sendSuccess') and attempts and attempts[-1].get('success')
                 and len(wire) == 64 and wire[:4] == bytes.fromhex('81040302')
                 and value == int.from_bytes(wire[4:6], 'little')
                 and 2000 <= value <= 5000)
        if not valid:
            invalid.append(path.name)
            continue
        readings.append({'utc': attempts[-1]['utc'], 'millivolts': value})
        second_words.append(int.from_bytes(wire[6:8], 'little'))
    transitions = []
    previous = None
    with (session / 'input-reports.jsonl').open(encoding='utf-8-sig') as lines:
        for line in lines:
            item = json.loads(line)
            wire = bytes.fromhex(item['hex'])
            if len(wire) != 64 or wire[0] != 1:
                continue
            raw = wire[53]
            if raw != previous:
                transitions.append({'utc': item['utc'], 'elapsedMs': item['elapsedMs'],
                                    'batteryStatusHex': f'{raw:02X}'})
                previous = raw
    features = []
    for path in sorted(session.glob('feature-*-before.json')):
        before = json.loads(path.read_text(encoding='utf-8-sig'))
        after = json.loads(path.with_name(path.name.replace('-before', '-after')).read_text(encoding='utf-8-sig'))
        features.append({'reportId': before['reportId'], 'beforeSuccess': before['success'],
                         'afterSuccess': after['success'],
                         'sameData': before['hex'] == after['hex'] if before['success'] and after['success'] else None})
    return {'session': session.name, 'samples': summary['samples'], 'failedReads': summary['failures'],
            'actualSeconds': summary['actualSeconds'], 'hashMismatches': mismatches,
            'batteryStatusByteCounts': summary['batteryStatusByteCounts'], 'transitions': transitions,
            'voltageQueryCount': len(readings) + len(invalid), 'validVoltageReadings': len(readings),
            'invalidVoltageFiles': invalid, 'firstVoltage': readings[0] if readings else None,
            'lastVoltage': readings[-1] if readings else None,
            'minMillivolts': min((r['millivolts'] for r in readings), default=None),
            'maxMillivolts': max((r['millivolts'] for r in readings), default=None),
            'distinctMillivolts': len({r['millivolts'] for r in readings}),
            'secondWordRawRange': [min(second_words), max(second_words)] if second_words else None,
            'features': features}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('sessions', nargs='+', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    result = {'sessions': [analyze(path) for path in args.sessions],
              'limitation': 'Voltage units follow the cited hardware tester; no independent voltmeter calibration. Second word is not verified current. No exact SOC or watts derived.'}
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 1 if any(s['hashMismatches'] or s['invalidVoltageFiles'] for s in result['sessions']) else 0


if __name__ == '__main__':
    raise SystemExit(main())
