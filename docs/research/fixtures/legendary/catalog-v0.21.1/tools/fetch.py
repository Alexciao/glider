# Anonymous (client_credentials) catalog fetch, mirroring legendary's EPCAPI.start_session(client_credentials=True)
# and EPCAPI.get_game_info(). Token is kept in memory only and never printed.
import json, sys, urllib.request, urllib.parse, base64, os
out = sys.argv[1]
CID, SEC = '34a02cf8f4414e29b15921876da36f9a', 'daafbccc737745039dffe53d94fc76cf'  # public EGL client (legendary api/egs.py:24-25)
UA = 'UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit'
def req(url, data=None, headers=None):
    r = urllib.request.Request(url, data=data, headers={'User-Agent': UA, **(headers or {})})
    with urllib.request.urlopen(r, timeout=20) as resp:
        return json.loads(resp.read())
tok = req('https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token',
          data=urllib.parse.urlencode(dict(grant_type='client_credentials', token_type='eg1')).encode(),
          headers={'Authorization': 'Basic ' + base64.b64encode(f'{CID}:{SEC}'.encode()).decode(),
                   'Content-Type': 'application/x-www-form-urlencoded'})['access_token']
H = {'Authorization': f'bearer {tok}'}
items = [a.split(':') for a in sys.argv[2:]]
for ns, cid in items:
    try:
        j = req(f'https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{ns}/bulk/items?'
                + urllib.parse.urlencode(dict(id=cid, includeDLCDetails='true', includeMainGameDetails='true', country='US', locale='en')), headers=H)
        json.dump(j, open(os.path.join(out, f'catalog-{ns}-{cid}.json'), 'w'), indent=2, sort_keys=True)
        print('ok', ns, cid, list(j)[:3])
    except Exception as e:
        print('fail', ns, cid, repr(e)[:200])
# overlay manifest (anonymous; legendary core.py:2150)
try:
    j = req('https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows/namespace/302e5ede476149b1bc3e4fe6ae45e50e/catalogItem/cc15684f44d849e89e9bf4cec0508b68/app/98bc04bc842e4906993fd6d6644ffb8d/label/Live', headers=H)
    for e in j.get('elements', []):
        for m in e.get('manifests', []):
            m.pop('queryParams', None)  # signed CDN params
    json.dump(j, open(os.path.join(out, 'launcher-assets-v2-eos-overlay.json'), 'w'), indent=2, sort_keys=True)
    print('ok overlay')
except Exception as e:
    print('fail overlay', repr(e)[:200])
