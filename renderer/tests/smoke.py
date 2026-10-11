"""Generate small glTF fixtures and exercise the real Vulkan renderer. No Python dependencies."""
from pathlib import Path
import argparse
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import zlib


def png(width, height, pixel):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    raw = b''.join(b'\0' + b''.join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')


def fixture(path, bad_index=False, cycle=False, missing_texture=False, normal_pixel=(128,128,255,255), supplied_tangents=False):
    positions, normals, uv, indices, tangents = [], [], [], [], []
    rows, columns = 16, 32
    for y in range(rows + 1):
        phi = y * math.pi / rows
        for x in range(columns + 1):
            theta = x * math.tau / columns
            point = (math.sin(phi)*math.cos(theta), math.cos(phi), math.sin(phi)*math.sin(theta))
            positions.extend(point)
            normals.extend(point)
            uv.extend((x/columns, y/rows))
            tangents.extend((-math.sin(theta), 0, math.cos(theta), 1))
    for y in range(rows):
        for x in range(columns):
            a = y * (columns+1) + x
            b = a + columns+1
            if y: indices.extend((a, a+1, b))
            if y != rows-1: indices.extend((a+1, b+1, b))
    if bad_index: indices[0] = len(positions)
    data = bytearray()
    views = []

    def add(raw):
        data.extend(b'\0' * (-len(data) % 4))
        views.append({'buffer': 0, 'byteOffset': len(data), 'byteLength': len(raw)})
        data.extend(raw)
        return len(views)-1

    pos = add(struct.pack('<' + 'f'*len(positions), *positions))
    normal = add(struct.pack('<' + 'f'*len(normals), *normals))
    texcoord = add(struct.pack('<' + 'f'*len(uv), *uv))
    index = add(struct.pack('<' + 'I'*len(indices), *indices))
    image = add(png(16, 16, lambda x,y: (255,255,255,255) if (x//4+y//4)%2 else (120,180,220,0)))
    normal_image = add(png(2, 2, lambda x,y: normal_pixel))
    mr_image = add(png(2, 2, lambda x,y: (255,255,255,255)))
    attributes = {'POSITION': 0, 'NORMAL': 1, 'TEXCOORD_0': 2}
    materials = []
    for color, metallic, roughness, alpha in [([.1,.5,.9,1],0,.8,'OPAQUE'),([.9,.6,.2,1],1,.2,'OPAQUE'),([.6,.85,.6,1],0,.5,'MASK')]:
        materials.append({'pbrMetallicRoughness': {'baseColorFactor': color, 'metallicFactor': metallic, 'roughnessFactor': roughness,
            'baseColorTexture': {'index':0}, 'metallicRoughnessTexture': {'index':2}}, 'normalTexture':{'index':1}, 'alphaMode':alpha, 'alphaCutoff':.5})
    asset = {'asset':{'version':'2.0'}, 'scene':0, 'scenes':[{'nodes':[0]}],
        'nodes':[{'children':[1,2,3]}] + [{'mesh':i,'translation':[(i-1)*2.4,0,0]} for i in range(3)],
        'meshes':[{'primitives':[{'attributes':attributes,'indices':3,'material':i}]} for i in range(3)],
        'materials':materials, 'images':[{'bufferView':v,'mimeType':'image/png'} for v in [image,normal_image,mr_image]],
        'textures':[{'source':i} for i in range(3)], 'bufferViews':views,
        'accessors':[{'bufferView':pos,'componentType':5126,'count':len(positions)//3,'type':'VEC3','min':[-1,-1,-1],'max':[1,1,1]},
            {'bufferView':normal,'componentType':5126,'count':len(normals)//3,'type':'VEC3'},
            {'bufferView':texcoord,'componentType':5126,'count':len(uv)//2,'type':'VEC2'},
            {'bufferView':index,'componentType':5125,'count':len(indices),'type':'SCALAR'}],
        'buffers':[{'byteLength':len(data)}]}
    # Duplicate display names must not merge resource ownership or lose scene nodes.
    for kind in ('nodes', 'meshes', 'materials', 'images'):
        for item in asset[kind]: item['name'] = 'duplicate'
    if supplied_tangents:
        tangent_view = add(struct.pack('<' + 'f'*len(tangents), *tangents))
        attributes['TANGENT'] = len(asset['accessors'])
        asset['accessors'].append({'bufferView':tangent_view,'componentType':5126,'count':len(tangents)//4,'type':'VEC4'})
        asset['buffers'][0]['byteLength'] = len(data)
    if cycle: asset['nodes'][1]['children'] = [0]
    if missing_texture: asset['images'][0] = {'uri':'missing-texture.png'}
    encoded = json.dumps(asset, separators=(',',':')).encode()
    encoded += b' ' * (-len(encoded)%4)
    data.extend(b'\0' * (-len(data)%4))
    body = struct.pack('<II',len(encoded),0x4e4f534a)+encoded+struct.pack('<II',len(data),0x004e4942)+data
    path.write_bytes(struct.pack('<III',0x46546c67,2,12+len(body))+body)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('renderer', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    executable = args.renderer.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment['VK_LAYER_VALIDATE_SYNC'] = '1'

    def run(name, arguments, expected=0, binary=executable):
        process = subprocess.run([str(binary), *map(str,arguments)], capture_output=True, text=True, timeout=60, env=environment)
        log = process.stdout + process.stderr
        (args.output / (name+'.log')).write_text(log)
        assert process.returncode == expected, f'{name}: exit {process.returncode}\n{log}'
        assert not re.search(r'Validation Error|VUID-|SYNC-HAZARD', log), f'{name}: Vulkan validation\n{log}'
        print(name, 'passed')
        return log

    run('geometry',['--self-test'])
    run('globe',['--smoke','--frames','12','--capture',args.output/'globe.ppm'])
    run('traffic-motion',['--smoke','--frames','36','--capture',args.output/'traffic-motion.ppm'])
    def cyan_pixels(path):
        header, pixels = path.read_bytes().split(b'\n255\n', 1)
        width, height = map(int, header.splitlines()[1].split())
        assert len(pixels) == width * height * 3
        return {i // 3 for i in range(0, len(pixels), 3) if pixels[i] < 110 and pixels[i+1] > 190 and pixels[i+2] > 215}
    before = cyan_pixels(args.output/'globe.ppm')
    after = cyan_pixels(args.output/'traffic-motion.ppm')
    assert before and after and len(before.symmetric_difference(after)) > 20, 'Ship pixels must move across frames'
    def background_stars(path):
        header, pixels = path.read_bytes().split(b'\n255\n', 1)
        width, height = map(int, header.splitlines()[1].split())
        return {(x,y) for y in range(8,48) for x in range(300,width-300)
                if min(pixels[(y*width+x)*3:(y*width+x)*3+3]) > 70}
    stars = background_stars(args.output/'globe.ppm')
    assert len(stars) >= 5, 'Procedural stars must be visible in the unobstructed background'
    assert stars == background_stars(args.output/'traffic-motion.ppm'), 'Stars must stay stable while traffic moves'
    good = args.output/'materials.glb'
    fixture(good)
    run('materials',['--smoke','--frames','12','--mesh',good,'--capture',args.output/'materials.ppm'])
    for supplied in (False, True):
        name = 'normal-authored' if supplied else 'normal-derived'
        model = args.output / (name + '.glb')
        fixture(model, normal_pixel=(204,128,230,255), supplied_tangents=supplied)
        run(name, ['--smoke','--frames','12','--mesh',model,'--capture',args.output/(name+'.ppm')])
    def scene_pixels(name):
        header, pixels = (args.output/(name+'.ppm')).read_bytes().split(b'\n255\n', 1)
        width, height = map(int, header.splitlines()[1].split())
        return b''.join(pixels[(y*width+width//3)*3:(y*width+2*width//3)*3] for y in range(height//3, 2*height//3))
    flat, derived, authored = map(scene_pixels, ('materials', 'normal-derived', 'normal-authored'))
    def difference(a, b): return sum(abs(x-y) for x,y in zip(a,b)) / len(a)
    changed = difference(flat, derived)
    assert changed > .2, 'Normal maps without authored tangents must change the shaded scene'
    assert difference(derived, authored) < changed, 'Derivative normals must agree with the authored frame better than a flat normal'
    # A BC7 mode-6 block with equal red endpoints: exercise DDS selection over a missing PNG fallback.
    bits, at = 64, 7
    for value in (127,127,0,0,0,0,127,127):
        bits |= value << at
        at += 7
    bits |= 1 << at | 1 << (at+1)
    dds = bytearray(148)
    for offset, value in {0:0x20534444,4:124,12:4,16:4,28:1,76:32,80:4,84:0x30315844,128:98,132:3,140:1}.items():
        struct.pack_into('<I',dds,offset,value)
    dds.extend(bits.to_bytes(16,'little'))
    (args.output/'red.dds').write_bytes(dds)
    raw = good.read_bytes()
    json_size = struct.unpack_from('<I',raw,12)[0]
    asset = json.loads(raw[20:20+json_size])
    (args.output/'environment.bin').write_bytes(raw[28+json_size:])
    asset['buffers'][0]['uri'] = 'environment.bin'
    image_index = len(asset['images'])
    asset['images'] += [{'uri':'missing-fallback.png'},{'uri':'red.dds'}]
    asset['textures'].append({'source':image_index,'extensions':{'MSFT_texture_dds':{'source':image_index+1}}})
    asset['materials'][0]['extensions'] = {'KHR_materials_pbrSpecularGlossiness':{
        'diffuseFactor':[1,1,1,1], 'diffuseTexture':{'index':len(asset['textures'])-1},
        'specularFactor':[.04,.04,.04], 'glossinessFactor':.3}}
    asset['extensionsUsed'] = ['MSFT_texture_dds','KHR_materials_pbrSpecularGlossiness','KHR_lights_punctual']
    environment_file = args.output/'environment.gltf'
    environment_file.write_text(json.dumps(asset))
    log = run('environment-dds',['--smoke','--frames','12','--environment',environment_file,'--capture',args.output/'environment-dds.ppm'])
    assert 'Failed to load GLTF image' not in log and 'Environment at original scale' in log
    pixels = (args.output/'environment-dds.ppm').read_bytes().split(b'\n255\n',1)[1]
    assert sum(r > g*1.4 and r > b*1.4 and r > 60 for r,g,b in zip(pixels[::3],pixels[1::3],pixels[2::3])) > 200, 'DDS diffuse texture must produce a red surface'
    asset['extensions'] = {'KHR_lights_punctual':{'lights':[{'type':'point','color':[0,0,1],'intensity':50000,'range':50}]}}
    asset['nodes'].append({'translation':[0,0,3],'extensions':{'KHR_lights_punctual':{'light':0}}})
    asset['scenes'][0]['nodes'].append(len(asset['nodes'])-1)
    environment_file.write_text(json.dumps(asset))
    log = run('environment-light',['--smoke','--frames','12','--environment',environment_file,'--capture',args.output/'environment-light.ppm'])
    assert 'Scene: 1 local lights' in log
    assert difference(scene_pixels('environment-dds'),scene_pixels('environment-light')) > 1, 'Imported lights must change the shaded geometry'
    (args.output/'red.dds').write_bytes(dds[:-1])
    log = run('environment-bad-dds',['--smoke','--environment',environment_file],1)
    assert 'Truncated DDS mip payload' in log
    (args.output/'red.dds').write_bytes(dds)
    shader_dir = args.output / 'badshader'
    (shader_dir/'shaders').mkdir(parents=True, exist_ok=True)
    shader_exe = shader_dir / executable.name
    shutil.copyfile(executable, shader_exe)
    for dll in executable.parent.glob('*.dll'): shutil.copyfile(dll, shader_dir/dll.name)
    for name, data in [('shader-empty',b''), ('shader-misaligned',b'\x03\x02\x23\x07'+bytes(17)), ('shader-magic',bytes(20))]:
        (shader_dir/'shaders'/'mesh.vert.spv').write_bytes(data)
        log = run(name, ['--smoke','--mesh',good], 1, binary=shader_exe)
        assert 'Cannot load shader:' in log, 'Invalid SPIR-V must be rejected before passing it to Vulkan'
    bad = args.output/'invalid-index.glb'
    fixture(bad,bad_index=True)
    run('invalid-index',['--smoke','--mesh',bad],1)
    cyclic = args.output/'cyclic.glb'
    fixture(cyclic,cycle=True)
    run('cyclic',['--smoke','--mesh',cyclic],1)
    missing = args.output/'missing-texture.glb'
    fixture(missing,missing_texture=True)
    log = run('missing-texture',['--smoke','--frames','4','--mesh',missing])
    assert 'Failed to load GLTF image' in log, 'Missing texture must report the fallback'
    run('bad-cli',['--frames','nonsense'],1)
    events = args.output/'fixture.events.json'
    fixture_script = Path(__file__).resolve().parent / 'pipeline_fixture.mjs'
    events.write_bytes(subprocess.check_output(['node', str(fixture_script)]))
    records = json.loads(events.read_text(encoding='utf-8'))['events']
    log = run('recorded-events',['--smoke','--frames','40','--events',events,'--capture',args.output/'events.ppm'])
    assert 'Recorded event replay: 10 records, ticks 0-15' in log
    assert 'Vehicle model loaded' not in log, 'Event mode needs no vehicle assets'
    run('events-mixed-modes',['--events',events,'--mesh',good],1)
    invalid_events = args.output/'duplicate-events.json'
    invalid_events.write_text(json.dumps([records[0],records[0]]))
    log = run('events-duplicate',['--smoke','--events',invalid_events],1)
    assert 'Duplicate event ID or sequence' in log
    log = run('events-end',['--smoke','--frames','80','--events',events,'--capture',args.output/'events-end.ppm'])
    assert 'Recorded event replay: 10 records, ticks 0-15' in log
    subprocess.run([sys.executable, '-I', str(fixture_script.parent.parent/'tools'/'neon_to_events.py'), '--self-test'], check=True)
    converter = Path(__file__).resolve().parent.parent / 'tools' / 'history_to_replay.py'
    subprocess.run([sys.executable, '-I', str(converter), '--self-test'], check=True)
    capitals = ['aster 40 -105','beryl -18 -58','cinder 50 15','dune 25 42','ember -27 135',
                'fjord 64 -22','grove 3 23','haven 25 110','ionia 8 80','jade 35 140']
    def replay_file(path, capitals):
        path.write_text('faultline-replay 1\nrun smoke RULE 1\n' + ''.join(f'country {c} monarchy\n' for c in capitals)
                        + 'route aster_to_jade aster jade 4\n'
                        + ''.join(f'day {d}\nship c1 food 3 aster_to_jade {3-d}\nevent jade Smoke caption\n' for d in range(3))
                        + 'end completed\n')
    replay_file(args.output/'replay.replay', capitals)
    log = run('replay',['--smoke','--frames','12','--replay',args.output/'replay.replay','--capture',args.output/'replay.ppm'])
    assert 'Replay: RULE run smoke, 3 days, 1 routes' in log, 'Replay must load'
    replay_file(args.output/'moved.replay', ['aster 41 -105'] + capitals[1:])
    log = run('replay-moved-geography',['--smoke','--replay',args.output/'moved.replay'],1)
    assert "not at the renderer's capital" in log, 'Replay with different geography must be rejected'
    run('removed-missile',['--scene','missile'],1)
    run('removed-ocean',['--scene','ocean'],1)
    run('ship',['--smoke','--frames','12','--scene','ship','--capture',args.output/'ship.ppm'])
    header, pixels = (args.output/'ship.ppm').read_bytes().split(b'\n255\n', 1)
    assert len(set(pixels[::3001])) > 50, 'Ship view must render varied sky and water, not a cleared frame'
    run('plane',['--smoke','--frames','12','--scene','plane'])
    run('meadow',['--smoke','--frames','12','--scene','meadow','--capture',args.output/'meadow.ppm'])
    run('meadow-later',['--smoke','--frames','24','--scene','meadow','--capture',args.output/'meadow-later.ppm'])
    def ground_pixels(name):
        # Lower two fifths, right of the control panel: the field.
        header, pixels = (args.output/(name+'.ppm')).read_bytes().split(b'\n255\n', 1)
        width, height = map(int, header.splitlines()[1].split())
        return b''.join(pixels[(y*width+width//3)*3:(y+1)*width*3] for y in range(height*3//5, height))
    field = ground_pixels('meadow')
    assert sum(field[1::3]) > sum(field[0::3]) and sum(field[1::3]) > 1.5*sum(field[2::3]), 'The meadow field must render green grass'
    assert len(set(field[::3001])) > 50, 'The meadow must render varied blades, not a flat fill'
    assert difference(field, ground_pixels('meadow-later')) > 1, 'The meadow walk and wind must change the field over time'
    log = run('ship-vehicle',['--smoke','--frames','12','--scene','ship','--ship-model',good,'--plane-model',good,'--capture',args.output/'ship-vehicle.ppm'])
    assert log.count('Vehicle model loaded') == 1 and 'no-such' not in log, 'Only the starting (ship) view loads its model; the plane model waits for first use'
    run('missing-vehicle',['--scene','ship','--ship-model',args.output/'no-such.glb'],1)
    run('bad-scene',['--scene','moon'],1)
    print('All checks passed; captures and logs:',args.output)


if __name__ == '__main__':
    main()
