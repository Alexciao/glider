"""Writes the tiny verify fixture used by RealLegendary/VerifyRealTests: a manifest produced by Legendary 0.21.1's own serializer.

Usage: <venv>/python -I make_verify_manifest.py <legendary-0.21.1-checkout> <out.manifest> [<out-game-dir>]
The venv needs pycryptodomex (Legendary's manifest module imports it). Nothing here touches the network.

The install it describes is three files whose bytes come from FilePattern (byte i = (i * 31 + seed) & 0xFF), so the C# tests
re-create the files at run time with the same rule instead of committing them. Keep the table below in sync with
VerifyFixture.Files in RealLegendary/VerifyFixture.cs.
"""
import hashlib
import importlib.metadata as _im
import os
import sys

_orig = _im.version
_im.version = lambda n: '0.21.1' if n == 'legendary-gl' else _orig(n)
sys.path.insert(0, sys.argv[1])
from legendary.models.manifest import CDL, FML, ChunkInfo, ChunkPart, CustomFields, FileManifest, Manifest, ManifestMeta  # noqa: E402

FILES = [('Game.exe', 4000, 7), ('Data/assets.bin', 70000, 11), ('Data/notes.txt', 150, 13)]


def pattern(size, seed):
    return bytes((i * 31 + seed) & 0xFF for i in range(size))


m = Manifest()
m.meta = ManifestMeta()
m.meta.feature_level = 21
m.meta.data_version = 2
m.meta.app_name = 'GliderVerify'
m.meta.build_version = '1.0.0-verify'
m.meta.launch_exe = 'Game.exe'
m.chunk_data_list = CDL()
m.chunk_data_list.version = 3
m.chunk_data_list._manifest_version = 21
m.file_manifest_list = FML()
m.file_manifest_list.version = 0
m.custom_fields = CustomFields()
for n, (name, size, seed) in enumerate(FILES):
    data = pattern(size, seed)
    c = ChunkInfo(manifest_version=21)
    c.guid = (0x1000 + n, 0x2000 + n, 0x3000 + n, 0x4000 + n)
    c.hash = 0x1122334455667788 + n
    c.sha_hash = hashlib.sha1(data).digest()
    c.group_num = n
    c.window_size = size
    c.file_size = size
    m.chunk_data_list.elements.append(c)
    fm = FileManifest()
    fm.filename = name
    fm.hash = hashlib.sha1(data).digest()
    fm.file_size = size
    fm.chunk_parts = [ChunkPart(guid=c.guid, offset=0, size=size, file_offset=0)]
    m.file_manifest_list.elements.append(fm)

data = m.write()
with open(sys.argv[2], 'wb') as f:
    f.write(data if isinstance(data, bytes) else b'')
if not data:
    m.write(open(sys.argv[2], 'wb'))
back = Manifest.read_all(open(sys.argv[2], 'rb').read())
print(len(back.file_manifest_list.elements), 'files', os.path.getsize(sys.argv[2]), 'bytes')
if len(sys.argv) > 3:
    for name, size, seed in FILES:
        p = os.path.join(sys.argv[3], *name.split('/'))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        open(p, 'wb').write(pattern(size, seed))
