# Anonymous catalog listing of a namespace's items (public catalog data). Token in memory only.
import json, sys, urllib.request, urllib.parse, base64, os
out = sys.argv[1]
CID, SEC = '34a02cf8f4414e29b15921876da36f9a', 'daafbccc737745039dffe53d94fc76cf'
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
for ns in sys.argv[2:]:
    try:
        j = req(f'https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{ns}/items?'
                + urllib.parse.urlencode(dict(start=0, count=200, country='US', locale='en', includeDLCDetails='true', includeMainGameDetails='true')), headers=H)
        els = j.get('elements', [])
        keep = [e for e in els if e.get('releaseInfo')]
        print(ns, 'items', len(els), 'with releaseInfo', len(keep))
        for e in keep:
            ca = e.get('customAttributes', {})
            print('  ', e['id'], e['title'][:40], 'main' if e.get('mainGameItem') else 'base', [r['appId'] for r in e['releaseInfo']],
                  'cs=' + str(ca.get('CloudSaveFolder', {}).get('value')), '3p=' + str(ca.get('ThirdPartyManagedApp', {}).get('value')),
                  'plt=' + str(ca.get('partnerLinkType', {}).get('value')))
        json.dump(j, open(os.path.join(out, f'nsitems-{ns}.json'), 'w'), indent=2, sort_keys=True)
    except Exception as e:
        print('fail', ns, repr(e)[:200])
