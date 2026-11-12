"""Produces the golden EGL files Glider's write-back must match byte-for-byte, by running Legendary 0.21.1's own
EGLManifest code (legendary/models/egl.py) and the exact json.dump call of legendary/lfs/egl.py:set_manifest.

Glider's EglService.ApplyExport == the field assignments below (Legendary's from_lgd_game, with the values Glider
doesn't track taken from the existing .item). Files are written with newline='\\r\\n', which is what Python's text
mode produces on Windows (the only platform where Legendary writes EGL files), so this script is reproducible
anywhere.

Run: python -I tools/egl-golden/make_goldens.py <legendary 0.21.1 source dir> <repo root>
"""
import json
import ntpath
import os
import sys
import types

# legendary/__init__.py reads installed package metadata; load the package without it (only models/ is used).
_pkg = types.ModuleType('legendary')
_pkg.__path__ = [os.path.join(sys.argv[1], 'legendary')]
sys.modules['legendary'] = _pkg
from legendary.models.egl import EGLManifest  # noqa: E402

FIX = os.path.join(sys.argv[2], 'tests', 'Glider.Core.Tests', 'Fixtures', 'Egl')


def load(name):
    # lfs/egl.py read_manifests: json.load(open(path, encoding='utf-8'))
    with open(os.path.join(FIX, name), encoding='utf-8') as f:
        return json.load(f)


def dump(name, data):
    # lfs/egl.py set_manifest: json.dump(manifest_data, f, indent=4, sort_keys=True)
    with open(os.path.join(FIX, name), 'w', encoding='utf-8', newline='\r\n') as f:
        json.dump(data, f, indent=4, sort_keys=True)


def export(base, version, size, tags, catalog, namespace, exe, path, guid):
    """from_lgd_game field by field; untracked values fall back to the existing .item (from_json)."""
    m = EGLManifest.from_json(base)
    if m.app_version_string != version:
        m.remainder.pop('ManifestHash', None)  # stale once the .egstore manifest is replaced (Glider decision)
    m.app_name = 'Fortnite'
    m.app_version_string = version
    # base_urls: kept (Glider doesn't track igame.base_urls)
    m.build_label = 'Live'
    if catalog is not None:
        m.catalog_item_id = catalog
    if namespace is not None:
        m.namespace = namespace
    m.display_name = m.display_name or 'Fortnite'
    m.install_location = path
    m.install_size = size
    if tags:
        m.install_tags = tags
    m.installation_guid = guid
    # launch_command: kept
    if exe is not None:
        m.executable = exe
    m.main_game_appname = 'Fortnite'
    m.app_folder_name = m.app_folder_name or ntpath.basename(path)
    m.manifest_location = f'{path}/.egstore'
    m.ownership_token = bool(m.ownership_token)  # igame.requires_ot is a bool
    m.staging_location = f'{path}/.egstore/bps'
    # can_run_offline: kept
    m.is_incomplete_install = False
    m.needs_validation = False
    return m.to_json()


def move(base, path):
    m = EGLManifest.from_json(base)
    m.install_location = path
    m.manifest_location = f'{path}/.egstore'
    m.staging_location = f'{path}/.egstore/bps'
    m.ownership_token = bool(m.ownership_token)
    return m.to_json()


GAME = 'D:\\Games\\Epic Games\\Fortnite'
NEW_VERSION = '++Fortnite+Release-33.20-CL-46000000-Windows'

dump('writeback-fortnite.golden.item', export(
    load('fortnite-egl.item'), NEW_VERSION, 101234567890, [], '4fe75bbc5a674f4f9b356b5c90567da5', 'fn',
    'FortniteGame/Binaries/Win64/FortniteLauncher.exe', GAME, '6A1B3C5D7E9F40A1B2C3D4E5F6A7B8C9'))

edge = load('edge-cases.item')
dump('writeback-edge.golden.item', export(
    edge, '++Fortnite+Release-33.30-CL-47000000-Windows', 5, ['', 'Startup'], None, None, None,
    edge['InstallLocation'], edge['InstallationGuid']))

dump('writeback-new.golden.item', export(
    {'AppName': 'Fortnite'}, NEW_VERSION, 42, ['Startup'], '4fe75bbc5a674f4f9b356b5c90567da5', 'fn',
    'FortniteGame/Binaries/Win64/FortniteLauncher.exe', 'F:\\FN', '0123456789ABCDEF0123456789ABCDEF'))

dump('move-fortnite.golden.item', move(load('fortnite-egl.item'), 'G:\\Moved\\Fortnite'))

# core.py egl_export: mancpn = dict(FormatVersion=0, AppName=..., CatalogItemId=..., CatalogNamespace=...)
dump('fortnite.golden.mancpn', dict(FormatVersion=0, AppName='Fortnite',
                                    CatalogItemId='4fe75bbc5a674f4f9b356b5c90567da5', CatalogNamespace='fn'))

# repr() of the edge-case floats, for PyJson.FloatRepr unit tests.
print(json.dumps([repr(x) for x in edge['Floats']]))
print('goldens written to', FIX)
