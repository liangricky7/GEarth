"""Convert a Faultline verbose history (.ndjson or .ndjson.gz, faultline.history.v1) into the renderer's
line-based replay. Streams line by line, so file size is bounded by output, not by agent count.

    python tools/history_to_replay.py run.ndjson.gz run.replay
    python tools/history_to_replay.py --self-test
"""
import collections
import gzip
import json
import sys
import tempfile
from pathlib import Path


def name(country_id):
    return country_id.capitalize()


def captions(events):
    """One short caption per story event; migrations are summed per country pair."""
    migrations = collections.Counter()
    for event in events:
        kind, payload, ids = event['event_type'], event.get('payload') or {}, event.get('entity_ids') or []
        if kind == 'conflict_started':
            yield payload['first'], f"{name(payload['first'])} and {name(payload['second'])} enter conflict"
        elif kind == 'election_result' and payload.get('replaced'):
            yield ids[0], f"{name(ids[0])} replaces its government (approval {payload['approval']:.0%})"
        elif kind == 'border_changed':
            # Closures name the controlling country; expiry reopenings carry only the edge, controlled by its destination.
            a, b = payload['edge_id'].split('_to_')
            controller = ids[1] if len(ids) > 1 else b
            yield controller, f"{name(controller)} {'reopens' if payload['open'] else 'closes'} the {name(a)}-{name(b)} route"
        elif kind == 'business_started':
            yield ids[-1], f"New {payload['sector']} business opens in {name(ids[-1])}"
        elif kind == 'business_closed':
            yield ids[-1], (f"{name(ids[-1])} {payload['sector']} business closes, "
                            f"{payload.get('workers_laid_off', 0)} laid off")
        elif kind == 'agent_migrated':
            migrations[payload['from'], payload['to']] += 1
    for (source, target), count in migrations.items():
        yield source, f"{count} {'person migrates' if count == 1 else 'people migrate'} from {name(source)} to {name(target)}"


def convert(source, target):
    source, target = Path(source), Path(target)
    if source.resolve() == target.resolve():
        raise ValueError('Input and output paths must differ')
    with tempfile.TemporaryDirectory(dir=target.parent) as directory:
        temporary = Path(directory) / 'validated.replay'
        _convert(source, temporary)
        temporary.replace(target)


def _convert(source, target):
    opener = gzip.open if str(source).endswith('.gz') else open
    with opener(source, 'rt', encoding='utf-8') as lines, open(target, 'w', encoding='utf-8', newline='\n') as out:
        first = next(lines, None)
        if first is None:
            raise ValueError('Empty history')
        header = json.loads(first)
        if header.get('type') != 'header' or header.get('format') != 'faultline.history.v1':
            raise ValueError('Not a faultline.history.v1 file')
        nation_column = {field: i for i, field in enumerate(header['nation_fields'])}
        ship_column = {field: i for i, field in enumerate(header['shipment_fields'])}
        city_country = {location['id']: location['country_id'] for location in header['locations']}
        out.write('faultline-replay 1\n')
        out.write(f"run {header['run_id']} {header['mode']} {header['seed']}\n")
        for country in header['countries']:
            out.write(f"country {country['id']} {country['lat']} {country['lon']} {country['government_id']}\n")
        for route in header['routes']:
            out.write(f"route {route['id']} {city_country[route['from']]} {city_country[route['to']]} {route['travel_ticks']}\n")
        status = 'unknown'
        for line in lines:
            frame = json.loads(line)
            status = frame.get('status', status)
            if frame['type'] == 'end':
                break
            if frame['type'] != 'day':
                raise ValueError('Expected a day or end record')
            out.write(f"day {frame['tick']}\n")
            for row in frame['nations']:
                values = [row[nation_column[field]] for field in ('id', 'residents', 'unemployed', 'approval', 'gini', 'wars')]
                out.write('nation {} {} {} {:.3f} {:.3f} {}\n'.format(*values))
            for row in frame['shipments']:
                if row[ship_column['edge']] is None:
                    continue
                values = [row[ship_column[field]] for field in ('id', 'good', 'quantity', 'edge', 'edge_remaining_ticks')]
                out.write('ship {} {} {} {} {}\n'.format(*values))
            for event in frame['events']:
                if event['event_type'] == 'border_changed':
                    out.write(f"border {event['payload']['edge_id']} {int(event['payload']['open'])}\n")
            for country, text in captions(frame['events']):
                out.write(f"event {country} {text}\n")
        else:
            raise ValueError('Incomplete history: missing end record')
        if any(line.strip() for line in lines):
            raise ValueError('Unexpected records after history end')
        out.write(f"end {status}\n")


def self_test():
    header = {'type': 'header', 'format': 'faultline.history.v1', 'run_id': 'r1', 'mode': 'RULE', 'seed': 7,
              'nation_fields': ['id', 'government', 'residents', 'unemployed', 'approval', 'government_cash', 'gini',
                                'unmet_needs', 'wars', 'stock'],
              'shipment_fields': ['id', 'good', 'quantity', 'buyer', 'seller', 'edge', 'edge_remaining_ticks'],
              'countries': [{'id': 'aster', 'lat': 40, 'lon': -105, 'government_id': 'monarchy'}],
              'locations': [{'id': 'aster_city', 'country_id': 'aster'}, {'id': 'jade_city', 'country_id': 'jade'}],
              'routes': [{'id': 'aster_to_jade', 'from': 'aster_city', 'to': 'jade_city', 'travel_ticks': 4}]}
    migrant = {'event_type': 'agent_migrated', 'entity_ids': ['a1', 'aster', 'jade'], 'payload': {'from': 'aster', 'to': 'jade'}}
    day = {'type': 'day', 'tick': 0, 'status': 'running',
           'nations': [['aster', 'monarchy', 9, 1, .5, 0, .25, 0, 0, {}]],
           'shipments': [['c1', 'food', 3, 'b', 's', 'aster_to_jade', 2], ['c2', 'food', 1, 'b', 's', None, 0]],
           'events': [migrant, migrant, {'event_type': 'border_changed', 'entity_ids': ['aster_to_jade', 'jade'],
                                         'payload': {'edge_id': 'aster_to_jade', 'open': False}},
                      {'event_type': 'border_changed', 'entity_ids': ['aster_to_jade'],
                       'payload': {'edge_id': 'aster_to_jade', 'open': True}}]}
    with tempfile.TemporaryDirectory() as directory:
        source, target = Path(directory) / 'h.ndjson.gz', Path(directory) / 'r.replay'
        with gzip.open(source, 'wt', encoding='utf-8') as file:
            for frame in (header, day, {'type': 'end', 'status': 'completed', 'nations': [], 'shipments': [], 'events': []}):
                file.write(json.dumps(frame) + '\n')
        convert(source, target)
        lines = target.read_text(encoding='utf-8').splitlines()
        good = target.read_bytes()
        for invalid in ([header, day], [{'type': 'invalid'}], [header, day, {'type': 'unexpected'}]):
            with gzip.open(source, 'wt', encoding='utf-8') as file:
                for frame in invalid:
                    file.write(json.dumps(frame) + '\n')
            try:
                convert(source, target)
            except ValueError:
                pass
            else:
                raise AssertionError('Invalid history accepted')
            assert target.read_bytes() == good, 'Invalid input replaced a valid replay'
        original = source.read_bytes()
        try:
            convert(source, source)
        except ValueError:
            pass
        else:
            raise AssertionError('Same input/output path accepted')
        assert source.read_bytes() == original
    assert lines[:4] == ['faultline-replay 1', 'run r1 RULE 7', 'country aster 40 -105 monarchy', 'route aster_to_jade aster jade 4']
    assert 'nation aster 9 1 0.500 0.250 0' in lines and 'ship c1 food 3 aster_to_jade 2' in lines
    assert not any(line.startswith('ship c2') for line in lines), 'shipments without an edge are skipped'
    assert lines.index('border aster_to_jade 0') < lines.index('border aster_to_jade 1'), 'border state in event order'
    assert 'event jade Jade closes the Aster-Jade route' in lines and 'event jade Jade reopens the Aster-Jade route' in lines
    assert 'event aster 2 people migrate from Aster to Jade' in lines and lines[-1] == 'end completed'
    print('history_to_replay self-test passed')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 3:
        convert(sys.argv[1], sys.argv[2])
    else:
        sys.exit(__doc__)
