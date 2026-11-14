# Writes fake (non-secret) metadata + user.json into a throw-away legendary config dir.
import json,sys,os
cfg=sys.argv[1]
meta=dict(app_name='FakeA',app_title='FakeA Title',base_urls=[],sidecar=None,achievements=None,
  asset_infos={'Windows':dict(app_name='FakeA',asset_id='FakeA',build_version='1.0',catalog_item_id='00000000000000000000000000000001',label_name='Live-Windows',namespace='fakens',metadata={},sidecar_rev=0)},
  metadata=dict(id='00000000000000000000000000000001',namespace='fakens',title='FakeA Title',
    customAttributes={'CanRunOffline':{'type':'STRING','value':'true'},'FolderName':{'type':'STRING','value':'FakeA'},'AdditionalCommandLine':{'type':'STRING','value':'-meta_arg'}},
    categories=[{'path':'games'},{'path':'applications'}],keyImages=[]))
json.dump(meta,open(os.path.join(cfg,'metadata','FakeA.json'),'w'),indent=2)
user=dict(displayName='FakeUser',account_id='0123456789abcdef0123456789abcdef',access_token='FAKE',refresh_token='FAKE',
  expires_at='2000-01-01T00:00:00.000Z',expires_in=0,refresh_expires_at='2000-01-01T00:00:00.000Z',token_type='bearer')
json.dump(user,open(os.path.join(cfg,'user.json'),'w'),indent=2)
