"""Writes the EGL fixture inputs under tests/Glider.Core.Tests/Fixtures/Egl (EGL style: tab indent, CRLF).

The values are reconstructed from Legendary's EGLManifest schema plus keys EGL is known to write; see docs/egl.md
("Fixtures") for the confidence notes. Run: python -I tools/egl-golden/make_fixtures.py <repo root>
"""
import json
import os
import sys

OUT = os.path.join(sys.argv[1], 'tests', 'Glider.Core.Tests', 'Fixtures', 'Egl')
os.makedirs(OUT, exist_ok=True)

GAME = 'D:\\Games\\Epic Games\\Fortnite'


def write_egl_style(name, obj):
    s = json.dumps(obj, indent='\t', ensure_ascii=False).replace('\n', '\r\n')
    with open(os.path.join(OUT, name), 'wb') as f:
        f.write(s.encode('utf-8'))


# Realistic EGL-written Fortnite .item (EGL's own key order).
write_egl_style('fortnite-egl.item', {
    'FormatVersion': 0,
    'bIsIncompleteInstall': False,
    'LaunchCommand': '',
    'LaunchExecutable': 'FortniteGame/Binaries/Win64/FortniteLauncher.exe',
    'ManifestLocation': GAME + '/.egstore',
    'ManifestHash': '2c3a6c7e0d9f4b1a8e5d3c2b1a09f8e7d6c5b4a3',
    'bIsApplication': True,
    'bIsExecutable': True,
    'bIsManaged': False,
    'bNeedsValidation': False,
    'bRequiresAuth': True,
    'bAllowMultipleInstances': False,
    'bCanRunOffline': False,
    'bAllowUriCmdArgs': False,
    'bLaunchElevated': False,
    'BaseURLs': [
        'https://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/ChunksV4',
        'https://fastly-download.epicgames.com/Builds/Fortnite/CloudDir/ChunksV4',
        'https://download.epicgames.com/Builds/Fortnite/CloudDir/ChunksV4',
    ],
    'BuildLabel': 'Live',
    'AppCategories': ['public', 'games', 'applications'],
    'ChunkDbs': [],
    'CompatibleApps': [],
    'DisplayName': 'Fortnite',
    'InstallationGuid': '6A1B3C5D7E9F40A1B2C3D4E5F6A7B8C9',
    'InstallLocation': GAME,
    'InstallSessionId': '0D4C2B1A99884F7E8D6C5B4A39281706',
    'InstallTags': ['Startup', 'FrontEnd', 'Encrypted', 'Globals', 'DefaultGameplayChunk', 'GFP_BRRoot', 'FortniteBR', 'Langde'],
    'InstallComponents': [],
    'HostInstallationGuid': '00000000000000000000000000000000',
    'PrereqIds': [],
    'PrereqSHA1Hash': '',
    'LastPrereqSucceededSHA1Hash': '',
    'StagingLocation': GAME + '/.egstore/bps',
    'TechnicalType': 'games',
    'VaultThumbnailUrl': '',
    'VaultTitleText': '',
    'InstallSize': 98765432109,
    'MainWindowProcessName': '',
    'ProcessNames': [],
    'BackgroundProcessNames': [],
    'IgnoredProcessNames': [],
    'DlcProcessNames': [],
    'MandatoryAppFolderName': 'Fortnite',
    'OwnershipToken': 'false',
    'SidecarConfigRevision': 0,
    'CatalogNamespace': 'fn',
    'CatalogItemId': '4fe75bbc5a674f4f9b356b5c90567da5',
    'AppName': 'Fortnite',
    'AppVersionString': '++Fortnite+Release-33.10-CL-45000000-Windows',
    'MainGameCatalogNamespace': 'fn',
    'MainGameCatalogItemId': '4fe75bbc5a674f4f9b356b5c90567da5',
    'MainGameAppName': 'Fortnite',
    'AllowedUriEnvVars': [],
})

# Edge cases for the Python-format writer: unicode (BMP + astral), control chars, floats, big ints, nested dicts,
# keys whose order differs between case-insensitive and code-point sorting, OwnershipToken "True".
write_egl_style('edge-cases.item', {
    'AppName': 'Fortnite',
    'AppVersionString': '++Fortnite+Release-33.20-CL-46000000-Windows',
    'InstallLocation': 'E:\\Spi\u00ebl\u00e9 \u65e5\u672c/Fortnite',
    'InstallationGuid': 'ABCDEF0123456789ABCDEF0123456789',
    'DisplayName': 'Fortnite \u2122 \U0001F680',
    'OwnershipToken': 'True',
    'bCanRunOffline': True,
    'zz_lower': 1,
    'Zz_Upper': 2,
    '_underscore': 3,
    '\u00e9t\u00e9': 'accent key',
    '\U0001F600smile': 'astral key',
    '\uff21fullwidth': 'bmp key above surrogates',
    'Floats': [1.5, 0.1, 1e16, 1e15, 1e-05, 0.0001, 123456789.125, -2.0, 1.0, 3e300, 5e-324],
    'BigInt': 123456789012345678901234567890,
    'Control': 'tab\there "quoted" back\\slash\nnewline \u0001 \u007f /slash',
    'Nested': {'b': [], 'a': {}, 'C': {'y': None, 'x': [True, False]}},
    'InstallTags': [],
})

write_egl_style('LauncherInstalled.dat', {'InstallationList': [
    {'InstallLocation': 'C:\\Program Files\\Epic Games\\UE_5.4', 'NamespaceId': 'ue',
     'ItemId': 'f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6', 'ArtifactId': 'UE_5.4',
     'AppVersion': '5.4.4-35576357+++UE5+Release-5.4-Windows', 'AppName': 'UE_5.4'},
    {'InstallLocation': GAME, 'NamespaceId': 'fn',
     'ItemId': '4fe75bbc5a674f4f9b356b5c90567da5', 'ArtifactId': 'Fortnite',
     'AppVersion': '++Fortnite+Release-33.10-CL-45000000-Windows', 'AppName': 'Fortnite'},
]})
print('fixtures written to', OUT)
