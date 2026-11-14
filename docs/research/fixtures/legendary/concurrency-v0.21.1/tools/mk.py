import json,sys,os
p,games=sys.argv[1],sys.argv[2]
d={}
for n in ('FakeA','FakeB'):
    d[n]=dict(app_name=n,install_path=os.path.join(games,n),title=n+' Title',version='1.0',executable='game.exe',platform='Windows',install_size=1,is_dlc=False,can_run_offline=True,install_tags=[],base_urls=[],egl_guid='',launch_parameters='',manifest_path='',needs_verification=False,prereq_info=None,uninstaller=None,requires_ot=False,save_path=None,is_preloaded=False)
json.dump(d,open(p,'w'),indent=2)
