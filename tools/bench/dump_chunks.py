"""Chunk list of a Fortnite manifest, for CdnBench.cs and as a golden reference for a C# manifest reader.

Usage: <venv>/python -I tools/bench/dump_chunks.py <legendary-0.21.1-checkout> <file.manifest> <out.json>
Writes a JSON array of [path, compressedSize, windowSize] and prints a short summary (build, chunk and file counts,
sizes). Paths are relative to a CDN base URL such as https://<host>/Builds/Fortnite/CloudDir.
Copy the manifest out of %LocalAppData%\\Glider\\legendary\\manifests first; this script only reads it.
"""
import collections
import json
import os
import sys

sys.path.insert(0, sys.argv[1])
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lgd_shim  # noqa: E402,F401  (lets legendary import from a source checkout)
from legendary.models.manifest import Manifest  # noqa: E402

m = Manifest.read_all(open(sys.argv[2], 'rb').read())
chunks = m.chunk_data_list.elements
files = m.file_manifest_list.elements
compressed = [c.file_size for c in chunks]
print(f'{m.meta.build_version}: feature level {m.meta.feature_level}, {len(chunks)} chunks, {len(files)} files')
print(f'compressed {sum(compressed) / 2**30:.1f} GiB, window {sum(c.window_size for c in chunks) / 2**30:.1f} GiB, '
      f'window sizes {collections.Counter(c.window_size for c in chunks).most_common(2)}')
print(f'largest files (GiB): {[round(s / 2**30, 2) for s in sorted(f.file_size for f in files)[-5:]]}')
json.dump([[c.path, c.file_size, c.window_size] for c in chunks], open(sys.argv[3], 'w'))
