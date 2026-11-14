"""Apply Glider's proposed download patch to a Legendary 0.21.1 checkout (see docs/download-perf.md, section 5.4).

Usage: py -I tools/bench/patch_legendary.py <legendary-0.21.1-checkout>
Then `git -C <checkout> diff` shows the patch. Idempotent guard: refuses to run twice.

What it changes (all marked `# glider:` in the source):
  1. workers.py  DLWorker retry loop: `tries` is incremented (0.21.1 never did, so a failing chunk was retried
     forever, back to back, on the same CDN) and backoff is capped at 4 s; the manager builds workers with
     max_retries=3, so a chunk that fails 3 times goes back to the manager for a retry on the current CDN.
     The give-up path gets the `continue` it always lacked (it reported the failure twice, which inflated the
     manager's active-task count; 0.21.1 never reached it because of the retry bug).
  2. manager.py  CDN failover: DLManager keeps the ordered CDN list (selected one first, then the rest of the
     list Epic returned); 8 failed chunks within 30 s switch new and retried downloads to the next CDN.
  3. manager.py  control file (`--control-file <path>`): JSON polled once per progress interval,
     {"max_active": n} caps in-flight chunk downloads (1 .. 2*max_workers) at runtime, {"base_url": url}
     switches CDN without a restart. Lets the host app run adaptive concurrency and slow-CDN failover.
  4. manager.py  one machine-readable progress line per interval: `[DLManager] INFO:  + Glider\t- {json}`.
  5. core.py / cli.py: plumbing for 2 and 3.
"""
import sys

ROOT = sys.argv[1]


def sub(rel, old, new):
    path = f'{ROOT}/{rel}'
    s = open(path, encoding='utf-8').read()
    if s.count(old) != 1:
        raise SystemExit(f'{rel}: anchor not found exactly once: {old[:70]!r}')
    open(path, 'w', encoding='utf-8', newline='').write(s.replace(old, new))


W = 'legendary/downloader/mp/workers.py'
M = 'legendary/downloader/mp/manager.py'
C = 'legendary/core.py'
L = 'legendary/cli.py'

if '# glider:' in open(f'{ROOT}/{M}', encoding='utf-8').read():
    raise SystemExit('already patched')

# 1. retry loop
sub(W, "                        sleep_time = 2**(tries-1)\n",
       "                        sleep_time = min(2**(tries-1), 4)\n")
sub(W, "                    # print('Downloading', job.url)\n",
       "                    tries += 1  # glider: 0.21.1 never incremented this, so a failing chunk was retried forever\n")
sub(W, "                self.o_q.put(DownloaderTaskResult(success=False, **job.__dict__))\n"
       "            except KeyboardInterrupt:\n",
       "                self.o_q.put(DownloaderTaskResult(success=False, **job.__dict__))\n"
       "                continue  # glider: without this the failure was reported twice (unreachable in 0.21.1)\n"
       "            except KeyboardInterrupt:\n")

# 2. + 3. manager state
sub(M, "import logging\n", "import json\nimport logging\n")
sub(M, "                 max_shared_memory=1024 * 1024 * 1024, bind_ip=None, case_insensitive=True):",
       "                 max_shared_memory=1024 * 1024 * 1024, bind_ip=None, case_insensitive=True,\n"
       "                 fallback_urls=None, control_file=None):")
sub(M, "        self.base_url = base_url\n",
       "        self.base_url = base_url\n"
       "        # glider: ordered CDN list (selected first); switched on repeated failures or via the control file\n"
       "        self.base_urls = [base_url] + [u for u in (fallback_urls or []) if u != base_url]\n"
       "        self.base_idx = 0\n"
       "        self.cdn_failures = deque()\n"
       "        self.max_active = 0  # glider: 0 = max_workers * 2 (stock behaviour)\n"
       "        self.control_file = control_file\n"
       "        self._control_mtime = 0.0\n")
sub(M, "            while self.active_tasks < self.max_workers * 2 and self.chunks_to_dl:",
       "            while self.active_tasks < (self.max_active or self.max_workers * 2) and self.chunks_to_dl:")
sub(M, "self.dl_worker_queue.put(DownloaderTask(url=self.base_url + '/' + chunk.path,",
       "self.dl_worker_queue.put(DownloaderTask(url=self.base_urls[self.base_idx] + '/' + chunk.path,")
sub(M, "                        self.log.error(f'Download for {res.chunk_guid} failed, retrying...')\n",
       "                        self.log.error(f'Download for {res.chunk_guid} failed, retrying...')\n"
       "                        self._note_cdn_failure(res.url)  # glider: retry on the current (possibly new) CDN\n"
       "                        retry = DownloaderTask(url=self.base_urls[self.base_idx] + '/' +\n"
       "                                               self.chunk_data_list.get_chunk_by_guid(res.chunk_guid).path,\n"
       "                                               chunk_guid=res.chunk_guid, shm=res.shm)\n")
# glider: 0.21.1 re-queued the *result* object; the worker then calls DownloaderTaskResult(success=..., **job.__dict__)
# with 'success' already in __dict__ -> TypeError inside its except handler -> the worker process dies silently.
sub(M, "                            self.dl_worker_queue.put(res, timeout=1.0)\n",
       "                            self.dl_worker_queue.put(retry, timeout=1.0)  # glider: a fresh task, see above\n")
sub(M, "    def fw_results_handler(self, shm_cond: Condition):\n", '''    def _note_cdn_failure(self, failed_url):
        # glider: 8 failed chunks within 30 s on the current CDN => switch to the next one. Failures of requests
        # that were sent to an older CDN (still in flight when we switched) don't count against the new one.
        if not failed_url.startswith(self.base_urls[self.base_idx] + '/'):
            return
        now = time.time()
        self.cdn_failures.append(now)
        while self.cdn_failures and now - self.cdn_failures[0] > 30:
            self.cdn_failures.popleft()
        if len(self.cdn_failures) >= 8 and len(self.base_urls) > 1:
            old = self.base_urls[self.base_idx]
            self.base_idx = (self.base_idx + 1) % len(self.base_urls)
            self.cdn_failures.clear()
            self.log.warning(f'CDN switch: "{old.split("/")[2]}" -> "{self.base_urls[self.base_idx].split("/")[2]}" '
                             f'(8 failed chunk downloads in 30 s)')

    def _poll_control(self):
        # glider: {"max_active": n, "base_url": "https://host/..."}, written atomically (temp + rename) by the host
        if not self.control_file:
            return
        try:
            mtime = os.stat(self.control_file).st_mtime
            if mtime == self._control_mtime:
                return
            self._control_mtime = mtime
            with open(self.control_file, encoding='utf-8') as f:
                ctl = json.load(f)
        except (OSError, ValueError):
            return
        if isinstance(ctl.get('max_active'), int):
            self.max_active = max(1, min(ctl['max_active'], self.max_workers * 2))
        if isinstance(ctl.get('base_url'), str) and ctl['base_url'].startswith('http'):
            url = ctl['base_url'].rstrip('/')
            if url not in self.base_urls:
                self.base_urls.append(url)
            self.base_idx = self.base_urls.index(url)
            self.cdn_failures.clear()
        self.log.info(f'Control: max active {self.max_active or self.max_workers * 2}, '
                      f'CDN "{self.base_urls[self.base_idx].split("/")[2]}"')

    def fw_results_handler(self, shm_cond: Condition):
''')
# 4. machine-readable line + control polling, once per progress interval
sub(M, "            # send status update to back to instantiator (if queue exists)\n", '''            # glider: one machine-readable line per interval (the stock lines above stay unchanged)
            self.log.info(' + Glider\\t- ' + json.dumps({
                'cdn': self.base_urls[self.base_idx].split('/')[2], 'cdn_failures_30s': len(self.cdn_failures),
                'active': self.active_tasks, 'max_active': self.max_active or self.max_workers * 2,
                'cache_used_mib': round(total_used),
                'cache_total_mib': round(num_shared_memory_segments * self.analysis.biggest_chunk / 1024 / 1024),
                'dl_bps': round(dl_speed), 'write_bps': round(w_speed), 'read_bps': round(r_speed)}))
            self._poll_control()

            # send status update to back to instantiator (if queue exists)
''')
sub(M, "                         self.shared_memory.name, logging_queue=self.logging_queue,",
       "                         self.shared_memory.name, max_retries=3, logging_queue=self.logging_queue,  # glider")

# 5. plumbing
sub(C, "                         disable_https: bool = False, bind_ip: str = None) -> (DLManager, AnalysisResult, ManifestMeta):",
       "                         disable_https: bool = False, bind_ip: str = None,\n"
       "                         control_file: str = None) -> (DLManager, AnalysisResult, ManifestMeta):")
sub(C, "                        dl_timeout=dl_timeout, bind_ip=bind_ip, case_insensitive=platform.startswith('Win'))",
       "                        dl_timeout=dl_timeout, bind_ip=bind_ip, case_insensitive=platform.startswith('Win'),\n"
       "                        # glider: the rest of Epic's CDN list is the failover order\n"
       "                        fallback_urls=[u.replace('https://', 'http://') if disable_https else u for u in base_urls],\n"
       "                        control_file=control_file)")
sub(L, "                                                          preferred_cdn=args.preferred_cdn,",
       "                                                          preferred_cdn=args.preferred_cdn,\n"
       "                                                          control_file=args.control_file,  # glider")
sub(L, "    install_parser.add_argument('--preferred-cdn', dest='preferred_cdn', action='store', metavar='<hostname>',",
       "    install_parser.add_argument('--control-file', dest='control_file', action='store', metavar='<path>',\n"
       "                                help='[glider] JSON polled every second: {\"max_active\": n, \"base_url\": url}')\n"
       "    install_parser.add_argument('--preferred-cdn', dest='preferred_cdn', action='store', metavar='<hostname>',")
print('patched', ROOT)
