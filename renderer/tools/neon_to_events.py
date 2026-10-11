"""Convert a tagged Neon export into a recorded event timeline, without inventing daily state.

    python tools/neon_to_events.py export.json run.events.json
    python tools/neon_to_events.py --self-test
"""
import copy
import json
import math
import sys
import tempfile
from pathlib import Path


def require(condition, message):
    if not condition:
        raise ValueError(message)


def token(value):
    require(isinstance(value, str) and value and not any(c.isspace() for c in value), 'Invalid ID')
    return value


def integer(value, minimum=0):
    require(type(value) is int and minimum <= value <= 10000000, 'Invalid event integer')
    return value


def timeline(rows):
    require(isinstance(rows, list), 'Expected a tagged JSON array')
    records = []
    for row in rows:
        payload = row['payload']
        if isinstance(payload, str):
            payload = json.loads(payload)
        require(isinstance(payload, dict), 'Invalid record payload')
        records.append((row['record_type'], payload))
    runs = [payload for kind, payload in records if kind == 'stage1_runs']
    require(len(runs) == 1, 'Expected exactly one run snapshot')
    run, state = runs[0], runs[0]['state']
    run_id = token(run['run_id'])
    require(state['id'] == run_id and state['schema_version'] == 2, 'Invalid run state')
    last_seq, last_tick = integer(state['seq'], 1), integer(state['tick'])
    require(last_seq <= 100000, 'Event replay exceeds 100000 records')
    status = token(state['status'])
    for _, payload in records:
        require(payload['run_id'] == run_id, 'Cannot combine simulation runs')

    countries, country_ids = [], set()
    for country in state['countries']:
        cid = token(country['id'])
        require(cid not in country_ids, 'Duplicate country ID')
        country_ids.add(cid)
        for key, limit in [('lat', 90), ('lon', 180)]:
            value = country[key]
            require(type(value) in (int, float) and math.isfinite(value) and abs(value) <= limit,
                    'Invalid country coordinate')
        countries.append({key: country[key] for key in ('id', 'lat', 'lon')})
    require(countries, 'Missing country metadata')
    cities = {}
    for city in state['locations']:
        lid, cid = token(city['id']), token(city['country_id'])
        require(lid not in cities and cid in country_ids, 'Invalid location reference')
        cities[lid] = cid
    routes = []
    require(isinstance(state['routes'], dict), 'Invalid route metadata')
    for rid, route in state['routes'].items():
        require(token(rid) == route['id'], 'Route key does not match its ID')
        require(route['from'] in cities and route['to'] in cities, 'Unknown route endpoint')
        routes.append({'id': rid, 'from': cities[route['from']], 'to': cities[route['to']],
                       'travel_ticks': integer(route['travel_ticks'], 1)})
    route_ids = {route['id'] for route in routes}

    events, ids, sequences = [], set(), set()
    for kind, payload in records:
        if kind != 'stage1_events':
            continue
        event = payload['envelope']
        require(event['run_id'] == run_id and event['seq'] == payload['seq'],
                'Event envelope does not match its database row')
        seq, tick, eid = integer(event['seq'], 1), integer(event['tick']), token(event['event_id'])
        require(seq not in sequences and eid not in ids, 'Duplicate event ID or sequence')
        require(tick <= last_tick, 'Event occurs after the saved run state')
        sequences.add(seq)
        ids.add(eid)
        token(event['event_type'])
        require(isinstance(event['entity_ids'], list) and isinstance(event['payload'], dict),
                'Invalid event envelope')
        for entity in event['entity_ids']:
            token(entity)
        if event['event_type'] in ('shipment_dispatched', 'shipment_rerouted'):
            plan = event['payload']['plan']
            edges = plan['edge_ids']
            require(isinstance(edges, list) and edges and all(edge in route_ids for edge in edges),
                    'Shipment references an unknown route')
            integer(plan['travel_ticks'], 1)
            for first, second in zip(edges, edges[1:]):
                require(state['routes'][first]['to'] == state['routes'][second]['from'],
                        'Disconnected shipment route')
        events.append(event)
    require(sequences == set(range(1, last_seq + 1)), 'Incomplete event sequence: export every event for this run')
    events.sort(key=lambda event: event['seq'])
    require(all(a['tick'] <= b['tick'] for a, b in zip(events, events[1:])),
            'Event ticks decrease in sequence order')
    return {'format': 'faultline.events.v1', 'run_id': run_id, 'status': status,
            'countries': countries, 'routes': routes, 'events': events}


def convert(source, target):
    source, target = Path(source), Path(target)
    require(source.resolve() != target.resolve(), 'Input and output paths must differ')
    try:
        with source.open(encoding='utf-8-sig') as file:
            result = timeline(json.load(file))
        with tempfile.TemporaryDirectory(dir=target.parent) as directory:
            temporary = Path(directory) / 'events.json'
            with temporary.open('w', encoding='utf-8', newline='\n') as file:
                json.dump(result, file, ensure_ascii=False, separators=(',', ':'), allow_nan=False)
                file.write('\n')
            require(temporary.stat().st_size <= 64 * 1024 * 1024, 'Event JSON exceeds 64 MiB')
            temporary.replace(target)
    except (KeyError, TypeError, IndexError, AttributeError) as error:
        raise ValueError(f'Malformed Neon export: {error}') from error
    return result


def self_test():
    state = {'id': 'r1', 'schema_version': 2, 'seq': 2, 'tick': 3, 'status': 'completed',
             'countries': [{'id': 'aster', 'lat': 40, 'lon': -105}, {'id': 'jade', 'lat': 35, 'lon': 140}],
             'locations': [{'id': 'a', 'country_id': 'aster'}, {'id': 'j', 'country_id': 'jade'}],
             'routes': {'aj': {'id': 'aj', 'from': 'a', 'to': 'j', 'travel_ticks': 2}}}
    event = {'run_id': 'r1', 'seq': 1, 'event_id': 'r1:e1', 'tick': 1,
             'event_type': 'shipment_dispatched', 'entity_ids': ['cargo_1'],
             'payload': {'plan': {'edge_ids': ['aj'], 'travel_ticks': 2}, 'good': 'food', 'quantity': 3}}
    delivered = dict(event, seq=2, event_id='r1:e2', tick=3, event_type='shipment_delivered', payload={})
    rows = [{'record_type': 'stage1_runs', 'payload': {'run_id': 'r1', 'state': state}}]
    rows.extend({'record_type': 'stage1_events', 'payload': {'run_id': 'r1', 'seq': e['seq'], 'envelope': e}}
                for e in (delivered, event))
    with tempfile.TemporaryDirectory() as directory:
        source, target = Path(directory) / 'input.json', Path(directory) / 'events.json'
        for strings in (False, True):
            sample = copy.deepcopy(rows)
            if strings:
                for row in sample:
                    row['payload'] = json.dumps(row['payload'])
            source.write_text(json.dumps(sample), encoding='utf-8')
            result = convert(source, target)
            assert result['events'] == [event, delivered], 'Original event envelopes must be preserved'
            assert result['routes'] == [{'id': 'aj', 'from': 'aster', 'to': 'jade', 'travel_ticks': 2}]
        good = target.read_bytes()
        invalid = [rows[:-1], rows + [rows[-1]]]
        for key, value in [('run_id', 'another'), ('seq', 9)]:
            mixed = copy.deepcopy(rows)
            mixed[-1]['payload'][key] = value
            invalid.append(mixed)
        decreasing = copy.deepcopy(rows)
        decreasing[-1]['payload']['envelope']['tick'] = 3
        decreasing[1]['payload']['envelope']['tick'] = 2
        invalid.append(decreasing)
        missing_route = copy.deepcopy(rows)
        missing_route[-1]['payload']['envelope']['payload']['plan']['edge_ids'] = ['unknown']
        invalid.append(missing_route)
        for sample in invalid:
            source.write_text(json.dumps(sample), encoding='utf-8')
            try:
                convert(source, target)
            except ValueError:
                pass
            else:
                raise AssertionError('Invalid export was accepted')
            assert target.read_bytes() == good, 'Invalid input replaced the output'
    print('neon_to_events self-test passed')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 3:
        try:
            replay = convert(sys.argv[1], sys.argv[2])
        except (OSError, ValueError) as error:
            sys.exit(f'Event import failed: {error}')
        print(f"{replay['run_id']}: {len(replay['events'])} events, ticks "
              f"{replay['events'][0]['tick']}-{replay['events'][-1]['tick']}; recorded event timeline, not daily state frames.")
    else:
        sys.exit(__doc__)
