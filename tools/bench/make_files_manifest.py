"""Writes the golden manifest with a file list used by ManifestReaderTests (committed under tests/.../Fixtures/legendary/manifest/).

Usage: python -I tools/bench/make_files_manifest.py <legendary-0.21.1-checkout> <out.manifest> <out.json>
Builds a feature-level-21 manifest (file list version 2: MD5, MIME type and SHA-256 columns) whose files carry install tags,
one UTF-16 file name and multi-part files, through Legendary's own serializer, reads it back with Legendary's own reader, and
writes what `legendary verify` would add up (cli.py:1281-1293) for a few config.ini tag values, so the C# reader can be compared.
"""
import importlib.util
import json
import os
import random
import sys
import types

# models/manifest.py imports AES for encrypted manifests only; this one is not encrypted.
crypto = types.ModuleType('Cryptodome')
cipher = types.ModuleType('Cryptodome.Cipher')
cipher.AES = None
crypto.Cipher = cipher
sys.modules['Cryptodome'] = crypto
sys.modules['Cryptodome.Cipher'] = cipher

spec = importlib.util.spec_from_file_location('lgd_manifest', os.path.join(sys.argv[1], 'legendary', 'models', 'manifest.py'))
mf = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mf)

rnd = random.Random(4242)
m = mf.Manifest()
m.meta = mf.ManifestMeta()
m.meta.feature_level = 21
m.meta.data_version = 2
m.meta.app_name = 'GliderFiles'
m.meta.build_version = '2.0.0-test'
m.meta.launch_exe = 'Game.exe'
m.chunk_data_list = mf.CDL()
m.chunk_data_list.version = 3
m.chunk_data_list._manifest_version = 21
chunks = []
for i in range(12):
    c = mf.ChunkInfo(manifest_version=21)
    c.guid = tuple(rnd.getrandbits(32) for _ in range(4))
    c.hash = rnd.getrandbits(64)
    c.sha_hash = bytes(rnd.getrandbits(8) for _ in range(20))
    c.group_num = rnd.randrange(100)
    c.window_size = 1048576
    c.file_size = 900000 + i
    m.chunk_data_list.elements.append(c)
    chunks.append(c)

files = [
    ('Game.exe', [], [(0, 0, 1048576), (1, 0, 524288)]),
    ('Content/Paks/base.pak', ['Startup'], [(2, 0, 1048576), (3, 0, 1048576), (4, 0, 1000)]),
    ('Content/Paks/hd.pak', ['HighRes'], [(5, 0, 1048576), (6, 0, 1048576), (7, 0, 1048576)]),
    ('Content/Paks/lang-de.pak', ['Langde'], [(8, 0, 700000)]),
    ('Content/Paks/lang-fr.pak', ['Langfr'], [(8, 700000, 300000), (9, 0, 200000)]),
    ('Content/Paks/shared.pak', ['Startup', 'HighRes'], [(10, 0, 4096)]),
    ('Content/Movies/Intrö 日本.bk2', ['Langde', 'Langfr'], [(11, 0, 333333)]),
    ('Engine/empty.ini', [], []),
]
m.file_manifest_list = mf.FML()
m.file_manifest_list.version = 2
for name, tags, parts in files:
    fm = mf.FileManifest()
    fm.filename = name
    fm.hash = bytes(rnd.getrandbits(8) for _ in range(20))
    fm.flags = 0x4 if name.endswith('.exe') else 0
    fm.install_tags = list(tags)
    offset = 0
    for ci, off, size in parts:
        fm.chunk_parts.append(mf.ChunkPart(guid=chunks[ci].guid, offset=off, size=size, file_offset=offset))
        offset += size
    fm.file_size = offset
    fm.hash_md5 = bytes(16) if name.endswith('.exe') else b''
    fm.mime_type = 'application/octet-stream'
    fm.hash_sha256 = bytes(rnd.getrandbits(8) for _ in range(32))
    m.file_manifest_list.elements.append(fm)
m.file_manifest_list.count = len(files)
m.custom_fields = mf.CustomFields()

with open(sys.argv[2], 'wb') as f:
    m.write(f)
back = mf.Manifest.read_all(open(sys.argv[2], 'rb').read())


def verify_total(config_tags):
    """cli.py verify_game: the files it checks for an [app] install_tags value (None = no value) and their summed size."""
    elements = sorted(back.file_manifest_list.elements, key=lambda a: a.filename.lower())
    if config_tags is not None:
        install_tags = set(i.strip() for i in config_tags.split(','))
        picked = [f for f in elements if any(it in install_tags for it in f.install_tags) or not f.install_tags]
    else:
        picked = elements
    return {'files': len(picked), 'bytes': sum(f.file_size for f in picked)}


out = {
    'files': [[f.filename, f.install_tags, f.file_size] for f in back.file_manifest_list.elements],
    'verify': {
        '<none>': verify_total(None),
        'Startup,Langde,': verify_total('Startup,Langde,'),
        'HighRes': verify_total('HighRes'),
        ' Langfr , Startup': verify_total(' Langfr , Startup'),
    },
}
json.dump(out, open(sys.argv[3], 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(back.file_manifest_list.elements), 'files', os.path.getsize(sys.argv[2]), 'bytes')
