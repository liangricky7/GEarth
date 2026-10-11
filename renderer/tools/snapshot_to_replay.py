"""Import one completed Faultline database snapshot; this does not reconstruct history.

    python tools/snapshot_to_replay.py export.csv snapshot.replay
    python tools/snapshot_to_replay.py --self-test
"""
import collections
import copy
import csv
import json
import math
import sys
import tempfile
from pathlib import Path

from history_to_replay import captions, convert as convert_history


def require(condition, message):
    if not condition:
        raise ValueError(message)


def token(value):
    require(isinstance(value, str) and value and not any(c.isspace() for c in value),
            'IDs and replay tokens must be nonempty strings without whitespace')
    return value


def integer(value, minimum=0):
    require(type(value) is int and minimum <= value <= 2147483647, 'Invalid integer in snapshot')
    return value


def number(value, low, high):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high,
            'Invalid numeric value in snapshot')
    return value


def frames(state, run_id):
    require(state['schema_version'] == 2 and state['status'] == 'completed',
            'Expected a completed schema_version 2 snapshot')
    require(token(state['id']) == token(run_id), 'CSV run_id does not match state.id')
    tick, mode, seed = integer(state['tick']), token(state['mode']), integer(state['seed'])
    countries, locations = state['countries'], state['locations']
    require(isinstance(countries, list) and countries and isinstance(locations, list), 'Invalid geography')
    country_ids, city_country = set(), {}
    for country in countries:
        cid = token(country['id'])
        require(cid not in country_ids, 'Duplicate country ID')
        country_ids.add(cid)
        number(country['lat'], -90, 90)
        number(country['lon'], -180, 180)
        token(country['government_id'])
    for location in locations:
        lid, cid = token(location['id']), token(location['country_id'])
        require(lid not in city_country and cid in country_ids, 'Invalid location reference')
        city_country[lid] = cid
    routes = state['routes']
    require(isinstance(routes, dict), 'Invalid routes')
    for rid, route in routes.items():
        require(token(rid) == token(route['id']), 'Route key does not match its ID')
        require(route['from'] in city_country and route['to'] in city_country, 'Unknown route endpoint')
        integer(route['travel_ticks'], 1)
        require(type(route['open']) is bool, 'Invalid route open state')
    society = state['society']
    require(set(society['nations']) == country_ids, 'Snapshot must include every nation')
    wars = collections.Counter()
    for relation in society['relations'].values():
        require(relation['first'] in country_ids and relation['second'] in country_ids,
                'Unknown country in relation')
        require(type(relation['at_war']) is bool, 'Invalid war state')
        if relation['at_war']:
            wars.update((relation['first'], relation['second']))
    nations = []
    for cid, nation in society['nations'].items():
        require(nation['id'] == cid, 'Nation key does not match its ID')
        population, unemployed = integer(nation['population']), integer(nation['unemployed'])
        require(unemployed <= population, 'Unemployment exceeds population')
        nations.append([cid, population, unemployed, number(nation['approval'], 0, 1),
                        number(nation['gini_cash'], 0, 1), wars[cid]])
    shipments, shipment_ids = [], set()
    for cargo in society['cargo']:
        if cargo['status'] != 'in_transit':
            continue
        sid = token(cargo['id'])
        require(sid not in shipment_ids, 'Duplicate active shipment ID')
        shipment_ids.add(sid)
        edges, index = cargo['plan']['edge_ids'], integer(cargo['edge_index'])
        require(isinstance(edges, list) and index < len(edges), 'Invalid active cargo edge index')
        edge = token(edges[index])
        require(edge in routes, 'Unknown active cargo route')
        remaining = integer(cargo['edge_remaining_ticks'])
        require(remaining <= routes[edge]['travel_ticks'], 'Cargo remaining time exceeds route duration')
        shipments.append([sid, token(cargo['good']), integer(cargo['quantity'], 1), edge, remaining])
    events = []
    for event in state['events']:
        if integer(event['tick']) != tick:
            continue
        require(event['run_id'] == run_id, 'Event belongs to another run')
        if event['event_type'] == 'border_changed':
            require(event['payload']['edge_id'] in routes and type(event['payload']['open']) is bool,
                    'Invalid border event')
        events.append(event)
    for cid, text in captions(events):
        require(cid in country_ids and '\n' not in text and '\r' not in text, 'Invalid event caption')
    header = {'type': 'header', 'format': 'faultline.history.v1', 'run_id': run_id,
              'mode': mode, 'seed': seed, 'countries': countries, 'locations': locations,
              'routes': list(routes.values()),
              'nation_fields': ['id', 'residents', 'unemployed', 'approval', 'gini', 'wars'],
              'shipment_fields': ['id', 'good', 'quantity', 'edge', 'edge_remaining_ticks']}
    day = {'type': 'day', 'tick': tick, 'status': 'completed', 'nations': nations,
           'shipments': shipments, 'events': events}
    return header, day, {'type': 'end', 'status': 'completed'}


def convert(source, target):
    source, target = Path(source), Path(target)
    require(source.resolve() != target.resolve(), 'Input and output paths must differ')
    csv.field_size_limit(2147483647)
    try:
        with source.open(encoding='utf-8-sig', newline='') as file:
            rows = csv.DictReader(file)
            require({'run_id', 'updated_at', 'state'} <= set(rows.fieldnames or []), 'Invalid CSV header')
            row = next(rows, None)
            require(row is not None and next(rows, None) is None, 'Expected exactly one snapshot CSV row')
            state = json.loads(row['state'])
        header, day, end = frames(state, row['run_id'])
        # Both temporary files stay beside the destination so replacement is atomic.
        with tempfile.TemporaryDirectory(dir=target.parent) as directory:
            history, replay = Path(directory) / 'snapshot.ndjson', Path(directory) / 'snapshot.replay'
            history.write_text(''.join(json.dumps(frame) + '\n' for frame in (header, day, end)), encoding='utf-8')
            convert_history(history, replay)
            lines = replay.read_text(encoding='utf-8').splitlines()
            # Current route state wins over any retained event from earlier in this day.
            lines[-1:-1] = [f"border {rid} {int(route['open'])}" for rid, route in state['routes'].items()]
            replay.write_text('\n'.join(lines) + '\n', encoding='utf-8', newline='\n')
            replay.replace(target)
    except (KeyError, TypeError, IndexError, AttributeError, csv.Error) as error:
        raise ValueError(f'Malformed snapshot: {error}') from error
    return day['tick'], len(header['countries']), len(day['shipments'])


def self_test():
    state = {'schema_version': 2, 'status': 'completed', 'id': 'r1', 'mode': 'LIVE', 'seed': 7, 'tick': 365,
             'countries': [{'id': 'aster', 'lat': 40, 'lon': -105, 'government_id': 'monarchy'},
                           {'id': 'jade', 'lat': 35, 'lon': 140, 'government_id': 'monarchy'}],
             'locations': [{'id': 'a', 'country_id': 'aster'}, {'id': 'j', 'country_id': 'jade'}],
             'routes': {'aster_to_jade': {'id': 'aster_to_jade', 'from': 'a', 'to': 'j', 'travel_ticks': 4, 'open': False}},
             'society': {'nations': {cid: {'id': cid, 'population': 9, 'unemployed': 1, 'approval': .5, 'gini_cash': .25}
                                     for cid in ('aster', 'jade')},
                         'relations': {'aj': {'first': 'aster', 'second': 'jade', 'at_war': True}},
                         'cargo': [{'id': 'c1', 'status': 'in_transit', 'good': 'food', 'quantity': 3,
                                    'plan': {'edge_ids': ['aster_to_jade']}, 'edge_index': 0, 'edge_remaining_ticks': 0},
                                   {'id': 'done', 'status': 'delivered'}]},
             'events': [{'tick': 365, 'run_id': 'r1', 'event_type': 'border_changed', 'entity_ids': ['aster_to_jade'],
                         'payload': {'edge_id': 'aster_to_jade', 'open': True}},
                        {'tick': 364, 'run_id': 'r1', 'event_type': 'business_started', 'entity_ids': ['aster'],
                         'payload': {'sector': 'food'}}]}
    with tempfile.TemporaryDirectory() as directory:
        source, target = Path(directory) / 's.csv', Path(directory) / 'r.replay'
        def write(value, duplicate=False, run_id='r1'):
            with source.open('w', encoding='utf-8', newline='') as file:
                writer = csv.writer(file)
                writer.writerow(['run_id', 'updated_at', 'state'])
                writer.writerow([run_id, '2026-10-11', json.dumps(value)])
                if duplicate:
                    writer.writerow([run_id, '2026-10-11', json.dumps(value)])
        write(state)
        assert convert(source, target) == (365, 2, 1)
        good = target.read_text(encoding='utf-8')
        assert 'day 365\n' in good and 'day 0\n' not in good
        assert 'nation aster 9 1 0.500 0.250 1\n' in good
        assert 'ship c1 food 3 aster_to_jade 0\n' in good and 'ship done ' not in good
        assert good.endswith('border aster_to_jade 0\nend completed\n')
        assert 'New food business' not in good
        invalid_cargo = copy.deepcopy(state)
        invalid_cargo['society']['cargo'][0]['edge_index'] = 1
        for value, duplicate, run_id in [(state, True, 'r1'), (state, False, 'wrong'),
                                         (invalid_cargo, False, 'r1'), ({}, False, 'r1')]:
            write(value, duplicate, run_id)
            try:
                convert(source, target)
            except ValueError:
                pass
            else:
                raise AssertionError('Invalid snapshot was accepted')
            assert target.read_text(encoding='utf-8') == good, 'Invalid input replaced the output'
    print('snapshot_to_replay self-test passed')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 3:
        try:
            tick, countries, shipments = convert(sys.argv[1], sys.argv[2])
        except (OSError, ValueError) as error:
            sys.exit(f'Snapshot import failed: {error}')
        print(f'Snapshot day {tick}: {countries} countries, {shipments} active shipments; not a full timeline.')
    else:
        sys.exit(__doc__)
