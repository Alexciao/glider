# Public catalog samples (fetched 2026-11-14)

Fetched with `tools/fetch.py` / `tools/nslist.py` using an **anonymous** `client_credentials` token for the public EGL
client — the same grant Legendary itself uses for `eos-overlay` (`core.py:2147-2148`, `api/egs.py:114-116`). No user
account was involved; the token was kept in memory only. The request is the one Legendary's `EPCAPI.get_game_info`
sends (`api/egs.py:211-217`: `…/catalog/api/shared/namespace/{ns}/bulk/items?id=…&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en`).
The `metadata` field of Legendary's `metadata/<app_name>.json` is exactly `response[catalogItemId]` (`core.py:574,593`).

| file | what |
|---|---|
| `catalog-fn-4fe75bbc5a674f4f9b356b5c90567da5.json` | Fortnite base item (12 `dlcItemList` entries, mostly `addons/launchable`) |
| `catalog-df37f065…-4106b71233fa4b4ba3b04c818fce91d4.json` | Alien: Isolation base item: `CloudSaveFolder`, `CanRunOffline`, 7 installable DLC |
| `catalog-df37f065…-57cd8d4cb4524152a57192555063334a.json` | one Alien: Isolation DLC item (has `mainGameItem`) |
| `catalog-247d4d4b…-62da4c1440ae445093f1d650c3a5cc8c.json` | Castlevania Anniversary Collection: `CloudSaveFolder` with `{AppData}/../Roaming/…/{EpicID}/…` |
| `launcher-assets-v2-eos-overlay.json` | `launcher/api/public/assets/v2/…` response for the EOS overlay app (`queryParams` stripped) |
