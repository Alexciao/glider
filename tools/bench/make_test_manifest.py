"""Writes the small golden manifest used by ManifestReaderTests (committed under tests/.../Fixtures/legendary/).

Usage: <venv>/python -I tools/bench/make_test_manifest.py <legendary-0.21.1-checkout> <out.manifest> <out.paths.json>
Builds a feature-level-21 manifest with 64 chunks (fixed seed) through Legendary's own serializer, then writes the
chunk list as Legendary computes it (``chunk.path``, compressed size, window size) so the C# reader can be compared with it.
"""
import json
import os
import random
import sys

sys.path.insert(0, sys.argv[1])
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lgd_shim  # noqa: E402,F401
from legendary.models.manifest import CDL, FML, ChunkInfo, CustomFields, Manifest, ManifestMeta  # noqa: E402

rnd = random.Random(2113)
m = Manifest()
m.meta = ManifestMeta()
m.meta.feature_level = 21
m.meta.data_version = 2
m.meta.app_name = 'GliderTest'
m.meta.build_version = '1.0.0-test'
m.meta.launch_exe = 'Test.exe'
m.chunk_data_list = CDL()
m.chunk_data_list.version = 3
m.chunk_data_list._manifest_version = 21
for i in range(64):
    c = ChunkInfo(manifest_version=21)
    c.guid = tuple(rnd.getrandbits(32) for _ in range(4))
    c.hash = rnd.getrandbits(64)
    c.sha_hash = bytes(rnd.getrandbits(8) for _ in range(20))
    c.group_num = rnd.randrange(100)
    c.window_size = 1048576 if i % 5 else 524288 + i
    c.file_size = 1048642 if i % 5 else 300000 + i
    m.chunk_data_list.elements.append(c)
m.file_manifest_list = FML()
m.file_manifest_list.version = 0
m.custom_fields = CustomFields()
data = m.write()
with open(sys.argv[2], 'wb') as f:
    f.write(data if isinstance(data, bytes) else b'')
if not data:
    m.write(open(sys.argv[2], 'wb'))
back = Manifest.read_all(open(sys.argv[2], 'rb').read())
json.dump([[c.path, c.file_size, c.window_size] for c in back.chunk_data_list.elements], open(sys.argv[3], 'w'))
print(len(back.chunk_data_list.elements), 'chunks', os.path.getsize(sys.argv[2]), 'bytes')
