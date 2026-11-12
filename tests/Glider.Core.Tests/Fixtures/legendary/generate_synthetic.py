"""Regenerates the synthetic-*.txt / synthetic-*.json fixtures.

Legendary 0.21.1 was never run signed in for this project, so there are no captured logs of real installs,
verifies, sign-ins or launches. These fixtures are rendered here with the *verbatim* f-strings / logging
format from the Legendary 0.21.1 source (file:line cited next to each one) and plausible values, and written
the way Legendary writes them on Windows: CRLF line ends (text mode), stderr in cp1252 with
errors='backslashreplace', stdout in UTF-8 (cli.py:2721).

Run:  python -I generate_synthetic.py      (writes next to this file)
"""
import json
import os
import random

HERE = os.path.dirname(os.path.abspath(__file__))
HEADER = ('# Synthetic fixture rendered by generate_synthetic.py from the verbatim format strings of '
          'legendary 0.21.1 ({src}); values are plausible, not captured from a live run.\n')
MiB = 1024 * 1024
BUILD = '++Fortnite+Release-33.00-CL-38324456-Windows'
OLD_BUILD = '++Fortnite+Release-32.11-CL-37960145-Windows'


class Stream:
    def __init__(self, name, src, encoding='cp1252', header=True):
        self.path = os.path.join(HERE, name)
        self.encoding = encoding
        self.parts = [HEADER.format(src=src)] if header else []

    def raw(self, text):
        self.parts.append(text)

    def line(self, text):
        self.parts.append(text + '\n')

    def log(self, name, level, msg):
        # cli.py:54-57 / cli.py:70: '[%(name)s] %(levelname)s: %(message)s'
        self.line(f'[{name}] {level}: {msg}')

    def save(self):
        text = ''.join(self.parts).replace('\n', '\r\n')
        with open(self.path, 'wb') as f:
            f.write(text.encode(self.encoding, errors='backslashreplace'))


def summary(err, install_size, dl_size, uncompressed, reuse, unchanged):
    # cli.py:1042-1049
    err.log('cli', 'INFO', f'Install size: {install_size / 1024 / 1024:.02f} MiB')
    compression = (1 - (dl_size / uncompressed)) * 100
    err.log('cli', 'INFO', f'Download size: {dl_size / 1024 / 1024:.02f} MiB '
                           f'(Compression savings: {compression:.01f}%)')
    err.log('cli', 'INFO', f'Reusable size: {reuse / 1024 / 1024:.02f} MiB (chunks) / '
                           f'{unchanged / 1024 / 1024:.02f} MiB (unchanged / skipped)')
    err.log('cli', 'INFO', 'Downloads are resumable, you can interrupt the download with '
                           'CTRL-C and resume it using the same command later on.')


def block(err, processed_chunks, num_chunk_tasks, runtime, total_dl, total_write, dl_speed, dl_unc_speed,
          w_speed, r_speed, total_used, active_tasks):
    # downloader/mp/manager.py:749-775 (same arithmetic)
    perc = (processed_chunks / num_chunk_tasks) * 100
    if runtime and processed_chunks:
        average_speed = processed_chunks / runtime
        estimate = (num_chunk_tasks - processed_chunks) / average_speed
        hours, estimate = int(estimate // 3600), estimate % 3600
        minutes, seconds = int(estimate // 60), int(estimate % 60)
        rt_hours, runtime = int(runtime // 3600), runtime % 3600
        rt_minutes, rt_seconds = int(runtime // 60), int(runtime % 60)
    else:
        hours = minutes = seconds = 0
        rt_hours = rt_minutes = rt_seconds = 0
    log = lambda m: err.log('DLManager', 'INFO', m)
    log(f'= Progress: {perc:.02f}% ({processed_chunks}/{num_chunk_tasks}), '
        f'Running for {rt_hours:02d}:{rt_minutes:02d}:{rt_seconds:02d}, '
        f'ETA: {hours:02d}:{minutes:02d}:{seconds:02d}')
    log(f' - Downloaded: {total_dl / 1024 / 1024:.02f} MiB, '
        f'Written: {total_write / 1024 / 1024:.02f} MiB')
    log(f' - Cache usage: {total_used:.02f} MiB, active tasks: {active_tasks}')
    log(f' + Download\t- {dl_speed / 1024 / 1024:.02f} MiB/s (raw) '
        f'/ {dl_unc_speed / 1024 / 1024:.02f} MiB/s (decompressed)')
    log(f' + Disk\t- {w_speed / 1024 / 1024:.02f} MiB/s (write) / '
        f'{r_speed / 1024 / 1024:.02f} MiB/s (read)')


def preamble(err, resumed_files=0, update=False):
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')       # core.py:253
    if not update:
        err.log('cli', 'INFO', 'Saving install tags for "Fortnite" to config: ,Startup,FrontEnd,FortniteBR')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')   # cli.py:988
    err.log('Core', 'INFO', 'Parsing game manifest...')                         # core.py:1527
    err.log('Core', 'INFO', 'Install path: D:\\Games\\Fortnite')                # core.py:1602
    if resumed_files:
        err.log('DLM', 'INFO', 'Found previously interrupted download. Download will be resumed if possible.')  # manager.py:124
        err.log('DLM', 'INFO', f'Skipping {resumed_files} files based on resume data.')                         # manager.py:150
    err.log('DLM', 'INFO', 'Found 1874 files to skip based on install tag.')  # manager.py:181
    err.log('DLM', 'INFO', 'Remaining files after filtering: 3122')           # manager.py:214
    err.log('Core', 'INFO', 'Selected CDN: epicgames-download1.akamaized.net (https)')  # core.py:1643


def dlm_start(err):
    err.log('DLManager', 'INFO', 'Download Manager running with process-id: 14212')  # manager.py:621
    err.log('DLManager', 'INFO', 'Starting download workers...')                     # manager.py:675
    err.log('DLManager', 'INFO', 'Starting file writing worker...')                  # manager.py:688


def dlm_finish(err):
    err.log('DLManager', 'INFO', 'Waiting for installation to finish...')       # manager.py:792
    err.log('DLManager', 'INFO', 'All done! Download manager quitting...')      # manager.py:823


def run_blocks(err, tasks, dl_total, seconds, speed, start_chunk=0, every=1, stall_at=None, spam=0):
    rnd = random.Random(42)
    processed = start_chunk
    total_dl = 0.0
    total_write = 0.0
    for t in range(1, seconds + 1):
        if stall_at is not None and t >= stall_at:
            dl = 0.0
            for i in range(spam):
                err.log(f'DLWorker {i % 8 + 1}', 'WARNING',   # workers.py:107
                        f'Chunk download for {rnd.randint(10**9, 10**10)} failed: (ConnectionError(MaxRetryError('
                        f"\"HTTPSConnectionPool(host='epicgames-download1.akamaized.net', port=443): Max retries exceeded "
                        f"with url: /Builds/Fortnite/CloudDir/ChunksV4/22/0C4E0B8C5E8B1C2D_7F3A.chunk (Caused by "
                        f"NameResolutionError('Failed to resolve epicgames-download1.akamaized.net "
                        f"([Errno 11001] getaddrinfo failed)'))\"))), retrying...")
        else:
            dl = speed * (0.85 + 0.3 * rnd.random()) if t > 1 else 0.0
        dl = min(dl, dl_total - total_dl)   # never report more than the run's download size
        step = int(tasks * dl / dl_total) if dl else 0
        processed = min(tasks, processed + step)
        total_dl += dl
        total_write += dl * 1.62
        if t % every == 0 or t == seconds or t <= 3:
            block(err, processed, tasks, t, total_dl, total_write, dl, dl * 1.62, dl * 1.62, 0.0,
                  96.0 if dl else 0.0, 16 if dl else 0)
    return processed


def install_fresh():
    err = Stream('synthetic-install-fresh.stderr.txt', 'cli.py:988-1155, downloader/mp/manager.py:621-823')
    preamble(err)
    tasks, dl_total = 62018, 30522.53 * MiB
    summary(err, 49442.06 * MiB, dl_total, 49442.06 * MiB, 0, 0)
    dlm_start(err)
    # one block per second for the first 10 s, then every 30 s (a shortened but format-exact capture)
    rnd_seconds = 360
    run_blocks(err, tasks, dl_total, rnd_seconds, 86 * MiB, every=30)
    block(err, tasks, tasks, rnd_seconds + 1, dl_total, 49442.06 * MiB, 0, 0, 12 * MiB, 0, 0, 0)
    dlm_finish(err)
    err.log('cli', 'INFO', 'Finished installation process in 372.41 seconds.')  # cli.py:1155
    err.save()
    out = Stream('synthetic-install-fresh.stdout.txt', 'cli.py:1158-1160', encoding='utf-8')
    # _handle_postinstall with -y prints the prereq block but doesn't run it (cli.py:1158-1164)
    out.line('')
    out.line('This game lists the following prerequisites to be installed:')
    out.line('- EAC: FortniteGame/Binaries/Win64/EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe install 9d8ea3b85e7c4bde8bb0c36d79ab3a4a')
    out.line('')
    out.save()


def install_resume():
    err = Stream('synthetic-install-resume.stderr.txt', 'manager.py:124-150, cli.py:1042-1049')
    preamble(err, resumed_files=1210)
    tasks, dl_total = 37211, 18310.10 * MiB
    summary(err, 49442.06 * MiB, dl_total, 29650.00 * MiB, 0, 19792.06 * MiB)
    dlm_start(err)
    run_blocks(err, tasks, dl_total, 8, 90 * MiB)
    err.save()


def install_stall():
    err = Stream('synthetic-install-stall.stderr.txt', 'workers.py:88-118 (infinite retry), manager.py:766-775')
    preamble(err)
    tasks, dl_total = 62018, 30522.53 * MiB
    summary(err, 49442.06 * MiB, dl_total, 49442.06 * MiB, 0, 0)
    dlm_start(err)
    run_blocks(err, tasks, dl_total, 14, 80 * MiB, stall_at=6, spam=4)
    err.save()


def install_diskfull_midway():
    err = Stream('synthetic-install-diskfull.stderr.txt', 'workers.py:321, manager.py:590, cli.py:1155')
    preamble(err)
    tasks, dl_total = 2400, 1200.0 * MiB
    summary(err, 1900.0 * MiB, dl_total, 1900.0 * MiB, 0, 0)
    dlm_start(err)
    run_blocks(err, tasks, dl_total, 4, 90 * MiB)
    err.log('FileWorker', 'WARNING', "Something in writing a file failed: OSError(28, 'No space left on device')")  # workers.py:321
    err.log('DLManager', 'CRITICAL', 'Writing for FortniteGame/Content/Paks/pakchunk10-WindowsClient.ucas failed!')  # manager.py:590
    err.log('FileWorker', 'WARNING', "Job FortniteGame/Content/Paks/pakchunk10-WindowsClient.utoc failed with: OSError(28, 'No space left on device'), fetching next one...")  # workers.py:326
    err.log('DLManager', 'CRITICAL', 'Writing for FortniteGame/Content/Paks/pakchunk10-WindowsClient.utoc failed!')
    block(err, tasks, tasks, 15, dl_total, 1700 * MiB, 0, 0, 0, 0, 0, 0)
    dlm_finish(err)
    err.log('cli', 'INFO', 'Finished installation process in 15.02 seconds.')
    err.save()


def install_locked_file():
    err = Stream('synthetic-install-lockedfile.stderr.txt', 'workers.py:238-321, manager.py:590')
    preamble(err, update=True)
    tasks, dl_total = 800, 300.0 * MiB
    summary(err, 49442.06 * MiB, dl_total, 520.0 * MiB, 120 * MiB, 48800.0 * MiB)
    dlm_start(err)
    run_blocks(err, tasks, dl_total, 3, 90 * MiB)
    err.log('FileWorker', 'WARNING', "Something in writing a file failed: PermissionError(13, 'The process cannot "
                                     "access the file because it is being used by another process')")
    err.log('DLManager', 'CRITICAL', 'Writing for FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe failed!')
    dlm_finish(err)
    err.log('cli', 'INFO', 'Finished installation process in 5.40 seconds.')
    err.save()


def install_nospace():
    # core.py:1738-1743 failure text, printed by cli.py:1064-1069 to stdout; then the CRITICAL line, exit 1
    err = Stream('synthetic-install-nospace.stderr.txt', 'cli.py:1042-1069, core.py:1738-1743')
    preamble(err)
    summary(err, 49442.06 * MiB, 30522.53 * MiB, 49442.06 * MiB, 0, 0)
    err.log('cli', 'CRITICAL', 'Installation cannot proceed, exiting.')
    err.save()
    out = Stream('synthetic-install-nospace.stdout.txt', 'cli.py:1055-1067', encoding='utf-8')
    free_gib, required_gib = 12.34 * 1024 ** 3 / 1024 ** 3, 49442.06 / 1024
    out.line('\nInstallation requirements check returned the following results:')
    out.line(' - Warning: This game is not marked for offline use (may still work).')
    out.line(f' ! Failure: Not enough available disk space! {free_gib:.02f} GiB < {required_gib:.02f} GiB')
    out.line('')
    out.save()


def install_memoryerror():
    err = Stream('synthetic-install-memoryerror.stderr.txt', 'downloader/mp/manager.py:419-432 (uncaught MemoryError)')
    preamble(err)
    shared_mib, required = 2048.0, 2150.0 * MiB
    suggested = round(2048 + (required - 2048 * MiB) / MiB + 32)
    err.line('Traceback (most recent call last):')
    err.line('  File "cli.py", line 3262, in <module>')
    err.line('  File "cli.py", line 3177, in main')
    err.line('  File "cli.py", line 990, in install_game')
    err.line('  File "legendary\\core.py", line 1700, in prepare_download')
    err.line('  File "legendary\\downloader\\mp\\manager.py", line 431, in run_analysis')
    err.line(f'MemoryError: Current shared memory cache is smaller than required: {shared_mib:.01f} MiB < '
             f'{required / 1024 / 1024:.01f} MiB. Try running legendary with "--enable-reordering" to reduce memory '
             f'usage, or use "--max-shared-memory {suggested:.0f}" to increase the limit.')
    err.line("[PYI-20988:ERROR] Failed to execute script 'cli' due to unhandled exception!")
    err.save()


def install_sdl_failed():
    err = Stream('synthetic-install-sdl-failed.stderr.txt', 'core.py:449-453')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('Core', 'WARNING', "Downloading SDL data failed with ConnectionError(MaxRetryError(\"HTTPSConnectionPool("
                               "host='api.legendary.gl', port=443): Max retries exceeded with url: /v1/sdl/Fortnite.json\"))")
    err.log('cli', 'INFO', 'Saving install tags for "Fortnite" to config: ,chunk0,chunk10')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')
    err.save()


def update_uptodate():
    err = Stream('synthetic-update-uptodate.stderr.txt', 'cli.py:1012-1040')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('cli', 'INFO', 'Using install tags from config: ,Startup,FrontEnd,FortniteBR')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')
    err.log('Core', 'INFO', 'Parsing game manifest...')
    err.log('cli', 'INFO', 'Download size is 0, the game is either already up to date or has not changed. Exiting...')
    err.save()


def update_patch():
    err = Stream('synthetic-update-patch.stderr.txt', 'cli.py:988-1155, manager.py:766-775')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('cli', 'INFO', 'Using install tags from config: ,Startup,FrontEnd,FortniteBR')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')
    err.log('Core', 'INFO', 'Parsing game manifest...')
    err.log('Core', 'INFO', 'Install path: D:\\Games\\Fortnite')
    tasks, dl_total = 4412, 2210.48 * MiB
    summary(err, 49870.20 * MiB, dl_total, 3501.0 * MiB, 812.33 * MiB, 46120.75 * MiB)
    dlm_start(err)
    run_blocks(err, tasks, dl_total, 26, 88 * MiB)
    block(err, tasks, tasks, 27, dl_total, 3501.0 * MiB, 0, 0, 40 * MiB, 30 * MiB, 0, 0)
    dlm_finish(err)
    err.log('cli', 'INFO', 'Finished installation process in 28.11 seconds.')
    err.save()


def install_partial_block():
    err = Stream('synthetic-install-partialblock.stderr.txt', 'manager.py:766-775 with lines dropped')
    tasks = 1000
    log = lambda m: err.log('DLManager', 'INFO', m)
    log('= Progress: 10.00% (100/1000), Running for 00:00:10, ETA: 00:01:30')
    log(' - Downloaded: 50.00 MiB, Written: 80.00 MiB')
    # block truncated here (process output interleaved / lost)
    log('= Progress: 20.00% (200/1000), Running for 00:00:20, ETA: 00:01:20')
    log(' - Downloaded: 100.00 MiB, Written: 160.00 MiB')
    log(' - Cache usage: 96.00 MiB, active tasks: 16')
    log(' + Download\t- 5.00 MiB/s (raw) / 8.10 MiB/s (decompressed)')
    log(' + Disk\t- 8.10 MiB/s (write) / 0.00 MiB/s (read)')
    log('= Progress: 30.00% (300/1000), Running for 00:00:30, ETA: 00:01:10')
    err.save()


def lock_failed():
    err = Stream('synthetic-install-lock.stderr.txt', 'cli.py:845-848')
    err.log('cli', 'CRITICAL', 'Failed to acquire installed data lock, only one instance of Legendary may '
                               'install/import/move applications at a time.')
    err.save()


def login_expired():
    err = Stream('synthetic-install-login-expired.stderr.txt', 'core.py:266-271, cli.py:867-869')
    err.log('Core', 'INFO', 'Logging in...')
    err.log('Core', 'ERROR', 'Stored credentials are no longer valid! Please login again.')
    err.log('cli', 'ERROR', 'Login failed! Cannot continue with download process.')
    err.save()


def login_network():
    err = Stream('synthetic-launch-login-network.stderr.txt', 'core.py:272-274, cli.py:634-637')
    err.log('cli', 'INFO', 'Logging in...')
    err.log('Core', 'WARNING', "Checking for Legendary updates failed: ConnectionError(MaxRetryError(\"HTTPSConnectionPool"
                               "(host='api.legendary.gl', port=443): Max retries exceeded with url: /v1/version.json\"))")
    err.log('Core', 'INFO', 'Logging in...')
    err.log('Core', 'ERROR', "HTTP request for login failed: ConnectionError(MaxRetryError(\"HTTPSConnectionPool("
                             "host='account-public-service-prod03.ol.epicgames.com', port=443): Max retries exceeded with "
                             "url: /account/api/oauth/token\")), please try again later.")
    err.log('cli', 'ERROR', 'Login failed, cannot continue!')
    err.save()


def corrective_action():
    err = Stream('synthetic-auth-corrective.stderr.txt', 'api/egs.py:126-133, cli.py:208')
    err.log('cli', 'INFO', 'Testing existing login data if present...')
    err.log('EGS', 'ERROR', 'Please accept the updated EULA (PRIVACY_POLICY_ACCEPTANCE), open the following URL to take '
                            'action: https://www.epicgames.com/id/login/correction')
    err.log('Core', 'ERROR', 'Logging in failed with InvalidCredentialsError(\'errors.com.epicgames.oauth.corrective_action_required\'), please try again.')
    err.log('cli', 'ERROR', 'Login attempt failed, please see log for details.')
    err.save()


def downloader_exception():
    err = Stream('synthetic-install-downloader-exception.stderr.txt', 'cli.py:1088-1093')
    preamble(err)
    summary(err, 49442.06 * MiB, 30522.53 * MiB, 49442.06 * MiB, 0, 0)
    dlm_start(err)
    err.log('cli', 'INFO', 'Installation failed after 3.21 seconds.')
    err.log('cli', 'WARNING', "The following exception occurred while waiting for the downloader to finish: "
                              "BrokenPipeError(32, 'The pipe is being closed'). Try restarting the process, the resume "
                              "file will be used to start where it failed. If it continues to fail please open an issue on GitHub.")
    err.save()


def cdn_traceback():
    err = Stream('synthetic-install-cdn.stderr.txt', 'core.py:1430-1451')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')
    for host in ('epicgames-download1.akamaized.net', 'fastly-download.epicgames.com', 'cloudflare.epicgamescdn.com'):
        err.log('Core', 'WARNING', f'Unable to download manifest from "{host}" (status: 403), trying next URL...')  # core.py:1445
    err.line('Traceback (most recent call last):')
    err.line('  File "cli.py", line 3262, in <module>')
    err.line('  File "legendary\\core.py", line 1451, in get_cdn_manifest')
    err.line('ValueError: Unable to get manifest from any CDN URL, last result: 403 (Forbidden)')
    err.line("[PYI-1720:ERROR] Failed to execute script 'cli' due to unhandled exception!")
    err.save()


def permission_traceback():
    err = Stream('synthetic-install-permission.stderr.txt', 'core.py:1596-1600')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')
    err.log('Core', 'INFO', 'Parsing game manifest...')
    err.line('Traceback (most recent call last):')
    err.line('  File "cli.py", line 3262, in <module>')
    err.line('  File "legendary\\core.py", line 1598, in prepare_download')
    err.line('PermissionError: No write access to "C:\\Program Files\\Fortnite"')
    err.line("[PYI-1720:ERROR] Failed to execute script 'cli' due to unhandled exception!")
    err.save()


def repair():
    # cli.py:922-940 (verify first with -y), then a normal download of the bad files
    out = Stream('synthetic-repair.stdout.txt', 'cli.py:1315,1332', encoding='utf-8')
    total = 6
    for num in range(1, total + 1):
        out.raw(f'Verification progress: {num}/{total} ({num / total * 100:.01f}%) [{512.3:.1f} MiB/s]\t\r')
    out.raw('\n')
    out.save()
    err = Stream('synthetic-repair.stderr.txt', 'cli.py:922-1022, 1240-1346')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('cli', 'INFO', 'Game has not been verified yet.')
    err.log('cli', 'INFO', 'Loading installed manifest for "Fortnite"')
    err.log('cli', 'INFO', f'Verifying "Fortnite" version "{BUILD}"')
    err.log('cli', 'ERROR', 'File does not match hash: "FortniteGame/Content/Paks/pakchunk0-WindowsClient.ucas"')
    err.log('cli', 'ERROR', 'Verification failed, 1 file(s) corrupted, 0 file(s) are missing.')
    err.log('cli', 'INFO', 'Using install tags from config: ,Startup,FrontEnd,FortniteBR')
    err.log('cli', 'INFO', 'Preparing download for "Fortnite" (Fortnite)...')
    tasks, dl_total = 2100, 1010.0 * MiB
    summary(err, 49442.06 * MiB, dl_total, 1600.0 * MiB, 0, 47842.06 * MiB)
    dlm_start(err)
    run_blocks(err, tasks, dl_total, 12, 90 * MiB)
    dlm_finish(err)
    err.log('cli', 'INFO', 'Finished installation process in 14.92 seconds.')
    err.save()


def verify(name, total, corrupted=(), missing=(), locked=(), large=None):
    out = Stream(f'synthetic-verify-{name}.stdout.txt', 'cli.py:1315,1332, lfs/utils.py:126-145', encoding='utf-8')
    err = Stream(f'synthetic-verify-{name}.stderr.txt', 'cli.py:1240-1346, lfs/utils.py:157')
    err.log('cli', 'INFO', 'Loading installed manifest for "Fortnite"')
    err.log('cli', 'INFO', f'Verifying "Fortnite" version "{BUILD}"')
    sizes = [64 * MiB] * total
    for index, size_mib in (large or ()):
        sizes[index - 1] = size_mib * MiB
    total_size = sum(sizes)
    processed = 0
    speed = 0.0
    for num in range(1, total + 1):
        size = sizes[num - 1]
        if size > 512 * MiB:   # lfs/utils.py:126-145 large-file progress
            out.raw('\n')
            interval = (size / (1024 * 1024)) // 100
            i = 0
            pos = 0
            while pos < size:
                pos = min(size, pos + MiB)
                if i % interval == 0:
                    perc = (pos / size) * 100
                    out.raw(f'\r=> Verifying large file "FortniteGame/Content/Paks/pakchunk{num}-WindowsClient.ucas": {perc:.0f}% '
                            f'({pos / 1024 / 1024:.1f}/{size / 1024 / 1024:.1f} MiB) [{1024.0:.1f} MiB/s]\t')
                i += 1
            out.raw(f'\r=> Verifying large file "FortniteGame/Content/Paks/pakchunk{num}-WindowsClient.ucas": 100% '
                    f'({size / 1024 / 1024:.1f}/{size / 1024 / 1024:.1f} MiB) [{1024.0:.1f} MiB/s]\t\n')
        processed += size
        percentage = (processed / total_size) * 100.0
        speed = 980.4
        out.raw(f'Verification progress: {num}/{total} ({percentage:.01f}%) [{speed:.1f} MiB/s]\t\r')
        path = f'FortniteGame/Content/Paks/pakchunk{num}-WindowsClient.ucas'
        if num in corrupted:
            err.log('cli', 'ERROR', f'File does not match hash: "{path}"')
        elif num in missing:
            err.log('cli', 'ERROR', f'File is missing: "{path}"')
        elif num in locked:
            err.log('LFS Utils', 'CRITICAL', f'Could not verify "{path}"; opening failed with: PermissionError(13, '
                                             f"'The process cannot access the file because it is being used by another process')")
            err.log('cli', 'ERROR', f'Other failure (see log), treating file as missing: "{path}"')
    out.raw(f'Verification progress: {total}/{total} ({100.0:.01f}%) [{speed:.1f} MiB/s]\t\n')
    out.save()
    bad_missing = len(missing) + len(locked)
    if not corrupted and not bad_missing:
        err.log('cli', 'INFO', 'Verification finished successfully.')
    else:
        err.log('cli', 'ERROR', f'Verification failed, {len(corrupted)} file(s) corrupted, {bad_missing} file(s) are missing.')
        err.log('cli', 'INFO', 'Run "legendary repair Fortnite" to repair your game installation.')
    err.save()


def auth():
    ok = Stream('synthetic-auth-success.stderr.txt', 'cli.py:99-206')
    ok.log('cli', 'INFO', 'Testing existing login data if present...')
    ok.log('cli', 'INFO', 'Successfully logged in as "Åsa 日本"')   # non-cp1252 chars become \\uXXXX
    ok.save()
    bad = Stream('synthetic-auth-failed.stderr.txt', 'core.py:157-166, cli.py:208')
    bad.log('cli', 'INFO', 'Testing existing login data if present...')
    bad.log('Core', 'ERROR', "Logging in failed with InvalidCredentialsError('errors.com.epicgames.account."
                             "oauth.authorization_code_not_found'), please try again.")
    bad.log('cli', 'ERROR', 'Login attempt failed, please see log for details.')
    bad.save()
    valid = Stream('synthetic-auth-still-valid.stderr.txt', 'cli.py:99-104')
    valid.log('cli', 'INFO', 'Testing existing login data if present...')
    valid.log('Core', 'INFO', 'Trying to re-use existing login session...')
    valid.log('cli', 'INFO', 'Stored credentials are still valid, if you wish to switch to a different account, run '
                             '"legendary auth --delete" and try again.')
    valid.save()


def status():
    out = Stream('synthetic-status-signedin.stdout.txt', 'cli.py:1620-1627', encoding='utf-8')
    out.line(json.dumps(dict(account='Åsa 日本', games_available=3, games_installed=1, egl_sync_enabled=False,
                             config_directory='C:/Users/tester/AppData/Local/Glider/legendary'), default=str))
    out.save()
    exp = Stream('synthetic-status-expired.stderr.txt', 'core.py:266-271, cli.py:1603-1606')
    exp.log('Core', 'INFO', 'Logging in...')
    exp.log('Core', 'ERROR', 'Stored credentials are no longer valid! Please login again.')
    exp.log('cli', 'ERROR', 'Log in failed!')
    exp.save()


def info():
    # cli.py:1700-1980 key names; values plausible for Fortnite
    tags = ['', 'CosmeticPreInstall', 'FortniteBR', 'FrontEnd', 'StartupOptional', 'Startup']
    disk = {'': 6_000_000_000, 'CosmeticPreInstall': 4_000_000_000, 'FortniteBR': 30_000_000_000,
            'FrontEnd': 2_000_000_000, 'Startup': 1_000_000_000, 'StartupOptional': 9_000_000_000}
    dl = {k: int(v * 0.62) for k, v in disk.items()}
    data = dict(
        game=dict(app_name='Fortnite', title='Fortnite', version=BUILD,
                  platform_versions={'Windows': BUILD}, grant_date='2018-05-01 12:00:00+00:00',
                  cloud_saves_supported=False, cloud_save_folder=None, cloud_save_folder_mac=None, is_dlc=False,
                  external_activation=None, launch_options=[], command_line=None, owned_dlc=[]),
        install=None,
        manifest=dict(size=61_234_567, type='binary', version=21, feature_level=21, compressed=True, encrypted=False,
                      app_name='Fortnite', launch_exe='FortniteGame/Binaries/Win64/FortniteLauncher.exe',
                      launch_command='', build_version=BUILD, build_id='4b6c6Q0mVrL3yU9k-Y7AqA==',
                      prerequisites=dict(ids=['9d8ea3b85e7c4bde8bb0c36d79ab3a4a'], name='EAC',
                                         path='FortniteGame/Binaries/Win64/EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe',
                                         args='install 9d8ea3b85e7c4bde8bb0c36d79ab3a4a'),
                      uninstaller=None, install_tags=tags, num_files=4996, num_chunks=61_000,
                      disk_size=sum(disk.values()), download_size=sum(dl.values()),
                      tag_disk_size=[dict(tag=t, size=disk[t], count=100) for t in tags],
                      tag_download_size=[dict(tag=t, size=dl[t], count=100) for t in tags],
                      is_preloaded=False))
    out = Stream('synthetic-info-fortnite.stdout.txt', 'cli.py:1700-1980 (info --json)', encoding='utf-8')
    out.line(json.dumps(data, default=str))
    out.save()


def launch():
    params = dict(game_parameters=[], game_executable='FortniteGame/Binaries/Win64/FortniteLauncher.exe',
                  game_directory='D:\\Games\\Fortnite',
                  egl_parameters=['-AUTH_LOGIN=unused', '-AUTH_PASSWORD=0123456789abcdef0123456789abcdef',
                                  '-AUTH_TYPE=exchangecode', '-epicapp=Fortnite', '-epicenv=Prod', '-EpicPortal',
                                  '-epicusername=Åsa 日本', '-epicuserid=0123456789abcdef0123456789abcdef',
                                  '-epiclocale=en', '-epicsandboxid=fn'],
                  launch_command=[], working_directory='D:\\Games\\Fortnite\\FortniteGame/Binaries/Win64',
                  user_parameters=[], environment={}, pre_launch_command='', pre_launch_wait=False)
    out = Stream('synthetic-launch-fortnite.stdout.txt', 'models/game.py:296-314, cli.py:687-688', encoding='utf-8')
    out.line(json.dumps(params, default=str))
    out.save()
    err = Stream('synthetic-launch-fortnite.stderr.txt', 'cli.py:634-642, core.py:886')
    err.log('cli', 'INFO', 'Logging in...')
    err.log('Core', 'INFO', 'Trying to re-use existing login session...')
    err.log('Core', 'INFO', 'Getting authentication token...')
    err.save()
    ood = Stream('synthetic-launch-outofdate.stderr.txt', 'cli.py:639-649')
    ood.log('cli', 'INFO', 'Logging in...')
    ood.log('Core', 'INFO', 'Trying to re-use existing login session...')
    ood.log('cli', 'INFO', 'Checking for updates...')
    ood.log('cli', 'ERROR', 'Game is out of date, please update or launch with update check skipping!')
    ood.save()


def imports():
    ok = Stream('synthetic-import-egl.stderr.txt', 'cli.py:1348-1446, core.py:1895-1924')
    ok.log('Core', 'INFO', 'Trying to re-use existing login session...')
    ok.log('Core', 'INFO', 'Found EGL install metadata, verifying...')
    ok.log('cli', 'INFO', 'Game install appears to be complete.')
    ok.log('cli', 'INFO', f'Installation had Epic Games Launcher metadata for version "{BUILD}", verification will not be required.')
    ok.log('cli', 'INFO', 'Game "Fortnite" has been imported.')
    ok.save()
    nofiles = Stream('synthetic-import-nofiles.stderr.txt', 'cli.py:1400-1410')
    nofiles.log('Core', 'INFO', 'Trying to re-use existing login session...')
    nofiles.log('Core', 'INFO', 'Downloading latest manifest for "Fortnite"')
    nofiles.log('cli', 'ERROR', 'No files belonging to Game "Fortnite" (Fortnite) found in the specified location, '
                                'please verify that the path is correct.')
    nofiles.log('cli', 'INFO', 'Did you mean "D:\\Games\\Fortnite"?')
    nofiles.save()
    exe = Stream('synthetic-import-noexe.stderr.txt', 'cli.py:1412-1416')
    exe.log('Core', 'INFO', 'Trying to re-use existing login session...')
    exe.log('cli', 'ERROR', 'Game executable could not be found at "D:\\Games\\Fortnite\\FortniteGame/Binaries/Win64/'
                            'FortniteLauncher.exe", please verify that the specified path is correct.')
    exe.save()
    already = Stream('synthetic-import-already.stderr.txt', 'cli.py:1360-1362')
    already.log('cli', 'ERROR', 'Game is already installed!')
    already.save()


def moves():
    ok = Stream('synthetic-move-ok.stderr.txt', 'cli.py:2644-2669')
    ok.log('cli', 'INFO', 'Moving "Fortnite" from "D:/Games" to "E:\\Games"')
    ok.log('cli', 'INFO', 'Not moving, just rewriting legendary metadata...')
    ok.log('cli', 'INFO', 'Finished.')
    ok.save()
    cross = Stream('synthetic-move-crossdrive.stderr.txt', 'cli.py:2654-2657')
    cross.log('cli', 'INFO', 'Moving "Fortnite" from "D:/Games" to "E:\\Games"')
    cross.log('cli', 'ERROR', 'Moving to a different drive is not supported. Move the folder manually to '
                              '"E:\\Games\\Fortnite" and run "legendary move Fortnite "E:\\Games" --skip-move"')
    cross.save()


def uninstall():
    ok = Stream('synthetic-uninstall-ok.stderr.txt', 'cli.py:1215-1218')
    ok.log('cli', 'INFO', 'Removing "Fortnite" from "D:\\Games\\Fortnite"...')
    ok.log('cli', 'INFO', 'Game has been uninstalled.')
    ok.save()
    locked = Stream('synthetic-uninstall-locked.stderr.txt', 'cli.py:1219-1220')
    locked.log('cli', 'INFO', 'Removing "Fortnite" from "D:\\Games\\Fortnite"...')
    locked.log('cli', 'WARNING', "Removing game failed: PermissionError(13, 'The process cannot access the file because "
                                 "it is being used by another process'), please remove D:\\Games\\Fortnite manually.")
    locked.save()


def files():
    installed = {'Fortnite': dict(
        app_name='Fortnite', install_path='D:\\Games\\Fortnite', title='Fortnite', version=BUILD,
        base_urls=['https://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/ChunksV4'],
        can_run_offline=False, egl_guid='', executable='FortniteGame/Binaries/Win64/FortniteLauncher.exe',
        install_size=51_843_123_456, install_tags=['', 'Startup', 'FrontEnd', 'FortniteBR'], is_dlc=False,
        launch_parameters='', manifest_path='', needs_verification=False, platform='Windows',
        prereq_info=dict(ids=['9d8ea3b85e7c4bde8bb0c36d79ab3a4a'], name='EAC',
                         path='FortniteGame/Binaries/Win64/EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe',
                         args='install 9d8ea3b85e7c4bde8bb0c36d79ab3a4a'),
        uninstaller=None, requires_ot=False, save_path=None, is_preloaded=False)}
    with open(os.path.join(HERE, 'synthetic-installed.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(installed, f, indent=2, sort_keys=True)   # lgndry.py:365
    user = dict(access_token='eg1~REDACTED', expires_in=7200, expires_at='2026-11-12T12:00:00.000Z',
                token_type='bearer', refresh_token='eg1~REDACTED', refresh_expires=1987200,
                refresh_expires_at='2026-12-05T10:00:00.000Z', account_id='0123456789abcdef0123456789abcdef',
                client_id='34a02cf8f4414e29b15921876da36f9a', internal_client=True, client_service='launcher',
                displayName='Åsa 日本', app='launcher', in_app_id='0123456789abcdef0123456789abcdef')
    with open(os.path.join(HERE, 'synthetic-user.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(user, f, indent=2, sort_keys=True)
    metadata = dict(app_name='Fortnite', app_title='Fortnite', asset_infos={'Windows': dict(
        app_name='Fortnite', asset_id='Fortnite', build_version=BUILD, catalog_item_id='4fe75bbc5a674f4f9b356b5c90567da5',
        label_name='Live-Windows', namespace='fn', metadata={}, sidecar_rev=2)},
        base_urls=[], sidecar=None, achievements=None,
        metadata=dict(id='4fe75bbc5a674f4f9b356b5c90567da5', namespace='fn', title='Fortnite',
                      customAttributes=dict(FolderName=dict(type='STRING', value='Fortnite'),
                                            CanRunOffline=dict(type='STRING', value='false')),
                      keyImages=[dict(type='DieselGameBox', url='https://cdn1.epicgames.com/offer/fn/box.jpg', width=2560, height=1440, md5='0', size=1),
                                 dict(type='DieselGameBoxTall', url='https://cdn1.epicgames.com/offer/fn/tall.jpg', width=1200, height=1600, md5='0', size=1),
                                 dict(type='DieselGameBoxLogo', url='https://cdn1.epicgames.com/offer/fn/logo.png', width=800, height=400, md5='0', size=1)]))
    with open(os.path.join(HERE, 'synthetic-metadata-Fortnite.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(metadata, f, indent=2, sort_keys=True)   # lgndry.py:267-271


if __name__ == '__main__':
    install_fresh()
    install_resume()
    install_stall()
    install_diskfull_midway()
    install_locked_file()
    install_nospace()
    install_memoryerror()
    install_sdl_failed()
    install_partial_block()
    update_uptodate()
    update_patch()
    lock_failed()
    login_expired()
    login_network()
    corrective_action()
    downloader_exception()
    cdn_traceback()
    permission_traceback()
    repair()
    verify('ok', 12)
    verify('failed', 12, corrupted=(3,), missing=(7, 8))
    verify('locked', 6, locked=(2,))
    verify('largefile', 6, large=((2, 1024), (4, 2048)))
    auth()
    status()
    info()
    launch()
    imports()
    moves()
    uninstall()
    files()
