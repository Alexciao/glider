"""Instrumented drop-ins for Legendary 0.21.1's downloader (legendary/downloader/mp/{manager,workers}.py).

The run() loops below are copies of the 0.21.1 originals with timers added and nothing else changed, so the
pipeline behaves exactly like the shipped exe. Each process logs one JSON stats record per second through
Legendary's own logging queue (logger name 'STATS'); legendary_harness.py collects them.

Importing this module replaces manager.DLWorker / manager.FileWorker with the instrumented classes. Child processes
are spawned (Windows) and unpickle IDLManager from this module, so the patch is applied in every process.

Per-process priority is taken from environment variables (inherited by spawned children):
  GLIDER_BENCH_WORKER_PRIO / GLIDER_BENCH_WRITER_PRIO / GLIDER_BENCH_MANAGER_PRIO =
  comma list of: below | idle | normal | bgmode | io:verylow|low|normal | mem:1..5
Optional experiments (default off, i.e. stock behaviour):
  GLIDER_BENCH_WRITER_BUFFER=<bytes>  open(path, 'wb', buffering=N) in the writer instead of the default.
"""
import ctypes
import json
import logging
import os
import sys
import time
from ctypes import wintypes
from logging.handlers import QueueHandler
from queue import Empty

import lgd_shim  # noqa: F401  (must run before any legendary import)

import urllib3.connectionpool  # noqa: E402

from legendary.downloader.mp import manager as _manager  # noqa: E402
from legendary.downloader.mp.workers import DLWorker, FileWorker  # noqa: E402
from legendary.lfs.wine_helpers import case_insensitive_file_search  # noqa: E402
from legendary.models.chunk import Chunk  # noqa: E402
from legendary.models.downloading import (  # noqa: E402
    DownloaderTask, DownloaderTaskResult, TaskFlags, TerminateWorkerTask, WriterTask, WriterTaskResult)

# ---------------------------------------------------------------- Windows priority helpers

PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000
PRIORITY = {'below': 0x4000, 'idle': 0x40, 'normal': 0x20}
IO = {'verylow': 0, 'low': 1, 'normal': 2}
ProcessIoPriority = 33
ProcessMemoryPriority = 0


class MEMORY_PRIORITY_INFORMATION(ctypes.Structure):
    _fields_ = [('MemoryPriority', wintypes.ULONG)]


def _k32():
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    k32.GetCurrentProcess.restype = wintypes.HANDLE
    k32.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    k32.GetPriorityClass.argtypes = [wintypes.HANDLE]
    k32.GetPriorityClass.restype = wintypes.DWORD
    k32.SetProcessInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    k32.GetProcessInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    return k32


def _ntdll():
    nt = ctypes.WinDLL('ntdll')
    nt.NtSetInformationProcess.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.ULONG]
    nt.NtQueryInformationProcess.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.ULONG, ctypes.c_void_p]
    return nt


def apply_priority(spec):
    if sys.platform != 'win32' or not spec:
        return
    k32 = _k32()
    ntdll = _ntdll()
    h = k32.GetCurrentProcess()
    for item in spec.split(','):
        item = item.strip()
        if item in PRIORITY:
            k32.SetPriorityClass(h, PRIORITY[item])
        elif item == 'bgmode':
            k32.SetPriorityClass(h, PROCESS_MODE_BACKGROUND_BEGIN)
        elif item.startswith('io:'):
            v = ctypes.c_ulong(IO[item[3:]])
            ntdll.NtSetInformationProcess(h, ProcessIoPriority, ctypes.byref(v), 4)
        elif item.startswith('mem:'):
            mp = MEMORY_PRIORITY_INFORMATION(int(item[4:]))
            k32.SetProcessInformation(h, ProcessMemoryPriority, ctypes.byref(mp), ctypes.sizeof(mp))


def read_priority():
    """(priority class, I/O priority 0-4, memory priority 0-5) of this process, as Windows reports them."""
    if sys.platform != 'win32':
        return None
    k32 = _k32()
    ntdll = _ntdll()
    h = k32.GetCurrentProcess()
    cls = k32.GetPriorityClass(h)
    io = ctypes.c_ulong(99)
    ntdll.NtQueryInformationProcess(h, ProcessIoPriority, ctypes.byref(io), 4, None)
    mp = MEMORY_PRIORITY_INFORMATION(99)
    k32.GetProcessInformation(h, ProcessMemoryPriority, ctypes.byref(mp), ctypes.sizeof(mp))
    names = {0x20: 'normal', 0x4000: 'below', 0x40: 'idle', 0x8000: 'above', 0x80: 'high'}
    return {'class': names.get(cls, hex(cls)), 'io': io.value, 'mem': mp.MemoryPriority}


# ---------------------------------------------------------------- connection counting (urllib3, per process)

NEW_CONNS = [0]
_orig_new_conn = urllib3.connectionpool.HTTPConnectionPool._new_conn


def _counting_new_conn(self):
    NEW_CONNS[0] += 1
    return _orig_new_conn(self)


urllib3.connectionpool.HTTPConnectionPool._new_conn = _counting_new_conn
urllib3.connectionpool.HTTPSConnectionPool._new_conn = _counting_new_conn


class _Stats:
    def __init__(self, kind, name, logger):
        self.kind, self.name, self.log = kind, name, logger
        self.t = {}
        self.n = {}
        self.last_emit = time.perf_counter()

    def add(self, key, dt):
        self.t[key] = self.t.get(key, 0.0) + dt

    def inc(self, key, v=1):
        self.n[key] = self.n.get(key, 0) + v

    def maybe_emit(self, force=False):
        now = time.perf_counter()
        if force or now - self.last_emit >= 1.0:
            rec = {'kind': self.kind, 'name': self.name, 'pid': os.getpid(), 'ts': time.time(),
                   'span': now - self.last_emit, 't': self.t, 'n': self.n}
            self.log.info(json.dumps(rec))
            self.t, self.n = {}, {}
            self.last_emit = now


def _stats_logger():
    lg = logging.getLogger('STATS')
    lg.setLevel(logging.INFO)
    return lg


# ---------------------------------------------------------------- download worker

class IDLWorker(DLWorker):
    def run(self):
        _root = logging.getLogger()
        _root.handlers = []
        _root.addHandler(QueueHandler(self.logging_queue))

        logger = logging.getLogger(self.name)
        logger.setLevel(self.log_level)
        apply_priority(os.environ.get('GLIDER_BENCH_WORKER_PRIO'))
        st = _Stats('dlworker', self.name, _stats_logger())
        st.log.info(json.dumps({'kind': 'prio', 'name': self.name, 'prio': read_priority()}))

        empty = False
        while True:
            st.maybe_emit()
            t0 = time.perf_counter()
            try:
                job: DownloaderTask = self.q.get(timeout=10.0)
                empty = False
            except Empty:
                st.add('idle', time.perf_counter() - t0)
                empty = True
                continue
            st.add('idle', time.perf_counter() - t0)

            if isinstance(job, TerminateWorkerTask):
                st.inc('conns', NEW_CONNS[0]); NEW_CONNS[0] = 0
                st.maybe_emit(force=True)
                break

            tries = 0
            compressed = 0
            chunk = None

            try:
                while tries < self.max_retries:
                    if tries > 1:
                        sleep_time = 2**(tries-1)
                        logger.info(f'Sleeping {sleep_time} seconds before retrying.')
                        t1 = time.perf_counter()
                        time.sleep(sleep_time)
                        st.add('backoff', time.perf_counter() - t1)

                    t1 = time.perf_counter()
                    try:
                        r = self.session.get(job.url, timeout=self.dl_timeout)
                        r.raise_for_status()
                    except Exception as e:
                        st.add('http_fail', time.perf_counter() - t1)
                        st.inc('retries')
                        logger.warning(f'Chunk download for {job.chunk_guid} failed: ({e!r}), retrying...')
                        # NB: 0.21.1 never increments `tries` here, so a failing chunk is retried forever
                        continue
                    st.add('http', time.perf_counter() - t1)
                    st.add('ttfb', r.elapsed.total_seconds())

                    if r.status_code != 200:
                        logger.warning(f'Chunk download for {job.chunk_guid} failed: status {r.status_code}, retrying...')
                        continue
                    else:
                        t1 = time.perf_counter()
                        compressed = len(r.content)
                        chunk = Chunk.read_buffer(r.content, self.secrets)
                        st.add('parse', time.perf_counter() - t1)
                        break
                else:
                    raise TimeoutError('Max retries reached')
            except Exception as e:
                logger.error(f'Job for {job.chunk_guid} failed with: {e!r}, fetching next one...')
                self.o_q.put(DownloaderTaskResult(success=False, **job.__dict__))
            except KeyboardInterrupt:
                logger.warning('Immediate exit requested, quitting...')
                break

            if not chunk:
                logger.warning('Chunk somehow None?')
                self.o_q.put(DownloaderTaskResult(success=False, **job.__dict__))
                continue

            try:
                t1 = time.perf_counter()
                data = chunk.data
                size = len(data)
                st.add('decode', time.perf_counter() - t1)
                if size > job.shm.size:
                    logger.fatal('Downloaded chunk is longer than SharedMemorySegment!')

                t1 = time.perf_counter()
                self.shm.buf[job.shm.offset:job.shm.offset + size] = data
                del chunk
                st.add('shm_copy', time.perf_counter() - t1)
                t1 = time.perf_counter()
                self.o_q.put(DownloaderTaskResult(success=True, size_decompressed=size,
                                                  size_downloaded=compressed, **job.__dict__))
                st.add('result_put', time.perf_counter() - t1)
                st.inc('chunks')
                st.inc('bytes', compressed)
                st.inc('conns', NEW_CONNS[0])
                NEW_CONNS[0] = 0
            except Exception as e:
                logger.warning(f'Job for {job.chunk_guid} failed with: {e!r}, fetching next one...')
                self.o_q.put(DownloaderTaskResult(success=False, **job.__dict__))
                continue
            except KeyboardInterrupt:
                logger.warning('Immediate exit requested, quitting...')
                break

        self.shm.close()


# ---------------------------------------------------------------- file writer

class IFileWorker(FileWorker):
    def run(self):
        _root = logging.getLogger()
        _root.handlers = []
        _root.addHandler(QueueHandler(self.logging_queue))

        logger = logging.getLogger(self.name)
        logger.setLevel(self.log_level)
        apply_priority(os.environ.get('GLIDER_BENCH_WRITER_PRIO'))
        st = _Stats('writer', self.name, _stats_logger())
        st.log.info(json.dumps({'kind': 'prio', 'name': self.name, 'prio': read_priority()}))
        buffering = int(os.environ.get('GLIDER_BENCH_WRITER_BUFFER', '-1'))

        last_filename = ''
        current_file = None

        while True:
            st.maybe_emit()
            try:
                t0 = time.perf_counter()
                try:
                    j: WriterTask = self.q.get(timeout=10.0)
                except Empty:
                    st.add('idle', time.perf_counter() - t0)
                    logger.warning('Writer queue empty!')
                    continue
                st.add('idle', time.perf_counter() - t0)

                if isinstance(j, TerminateWorkerTask):
                    if current_file:
                        current_file.close()
                    st.maybe_emit(force=True)
                    self.o_q.put(TerminateWorkerTask())
                    break

                t1 = time.perf_counter()
                path, filename = os.path.split(j.filename)
                file_dir = os.path.join(self.base_path, path)
                if self.case_insensitive:
                    file_dir = case_insensitive_file_search(file_dir)
                if not os.path.exists(file_dir):
                    os.makedirs(file_dir)
                full_path = os.path.join(file_dir, filename)
                st.add('meta', time.perf_counter() - t1)

                t1 = time.perf_counter()
                if j.flags & TaskFlags.CREATE_EMPTY_FILE:
                    open(full_path, 'a').close()
                    self.o_q.put(WriterTaskResult(success=True, **j.__dict__))
                    st.add('open', time.perf_counter() - t1)
                    continue
                if j.flags & TaskFlags.CREATE_SYMLINK:
                    raise NotImplementedError('symlinks are not used by Fortnite')
                elif j.flags & TaskFlags.OPEN_FILE:
                    if current_file:
                        logger.warning(f'Opening new file {j.filename} without closing previous! {last_filename}')
                        current_file.close()
                    current_file = open(full_path, 'wb', buffering=buffering)
                    last_filename = j.filename
                    self.o_q.put(WriterTaskResult(success=True, **j.__dict__))
                    st.add('open', time.perf_counter() - t1)
                    st.inc('files')
                    continue
                elif j.flags & TaskFlags.CLOSE_FILE:
                    if current_file:
                        current_file.close()
                        current_file = None
                    self.o_q.put(WriterTaskResult(success=True, **j.__dict__))
                    st.add('close', time.perf_counter() - t1)
                    continue
                elif j.flags & TaskFlags.RENAME_FILE:
                    if current_file:
                        current_file.close()
                        current_file = None
                    if j.flags & TaskFlags.DELETE_FILE:
                        try:
                            os.remove(full_path)
                        except OSError:
                            self.o_q.put(WriterTaskResult(success=False, **j.__dict__))
                            continue
                    try:
                        old_path = case_insensitive_file_search(os.path.join(self.base_path, j.old_file))
                        os.rename(old_path, full_path)
                    except OSError:
                        self.o_q.put(WriterTaskResult(success=False, **j.__dict__))
                        continue
                    self.o_q.put(WriterTaskResult(success=True, **j.__dict__))
                    st.add('rename', time.perf_counter() - t1)
                    continue
                elif j.flags & TaskFlags.DELETE_FILE:
                    if current_file:
                        current_file.close()
                        current_file = None
                    try:
                        os.remove(full_path)
                    except OSError:
                        pass
                    self.o_q.put(WriterTaskResult(success=True, **j.__dict__))
                    continue
                elif j.flags & TaskFlags.MAKE_EXECUTABLE:
                    self.o_q.put(WriterTaskResult(success=True, **j.__dict__))
                    continue

                try:
                    t1 = time.perf_counter()
                    if j.shared_memory:
                        shm_offset = j.shared_memory.offset + j.chunk_offset
                        shm_end = shm_offset + j.chunk_size
                        current_file.write(self.shm.buf[shm_offset:shm_end])
                        st.add('write', time.perf_counter() - t1)
                    elif j.cache_file:
                        with open(os.path.join(self.cache_path, j.cache_file), 'rb') as f:
                            if j.chunk_offset:
                                f.seek(j.chunk_offset)
                            current_file.write(f.read(j.chunk_size))
                        st.add('write_cache', time.perf_counter() - t1)
                    elif j.old_file:
                        with open(os.path.join(self.base_path, j.old_file), 'rb') as f:
                            if j.chunk_offset:
                                f.seek(j.chunk_offset)
                            current_file.write(f.read(j.chunk_size))
                        st.add('write_old', time.perf_counter() - t1)
                        st.inc('bytes_old', j.chunk_size)
                except Exception as e:
                    logger.warning(f'Something in writing a file failed: {e!r}')
                    self.o_q.put(WriterTaskResult(success=False, size=j.chunk_size, **j.__dict__))
                else:
                    t1 = time.perf_counter()
                    self.o_q.put(WriterTaskResult(success=True, size=j.chunk_size, **j.__dict__))
                    st.add('result_put', time.perf_counter() - t1)
                    st.inc('bytes', j.chunk_size)
                    st.inc('parts')
            except Exception as e:
                logger.warning(f'Job {j.filename} failed with: {e!r}, fetching next one...')
                self.o_q.put(WriterTaskResult(success=False, **j.__dict__))
                try:
                    if current_file:
                        current_file.close()
                        current_file = None
                except Exception as e:
                    logger.error(f'Closing file after error failed: {e!r}')
            except KeyboardInterrupt:
                if current_file:
                    current_file.close()
                return


# ---------------------------------------------------------------- manager (only the scheduler loop is timed)

class IDLManager(_manager.DLManager):
    def run(self):
        apply_priority(os.environ.get('GLIDER_BENCH_MANAGER_PRIO'))
        super().run()

    def download_job_manager(self, task_cond, shm_cond):
        st = _Stats('manager', 'download_job_manager', _stats_logger())
        st.log.info(json.dumps({'kind': 'prio', 'name': 'DLManager', 'prio': read_priority(),
                                'segments': len(self.sms)}))
        while self.chunks_to_dl and self.running:
            st.maybe_emit()
            while self.active_tasks < self.max_workers * 2 and self.chunks_to_dl:
                try:
                    sms = self.sms.popleft()
                    no_shm = False
                except IndexError:
                    no_shm = True
                    break

                c_guid = self.chunks_to_dl.popleft()
                chunk = self.chunk_data_list.get_chunk_by_guid(c_guid)
                try:
                    self.dl_worker_queue.put(DownloaderTask(url=self.base_url + '/' + chunk.path,
                                                            chunk_guid=c_guid, shm=sms),
                                             timeout=1.0)
                except Exception as e:
                    self.log.warning(f'Failed to add to download queue: {e!r}')
                    self.chunks_to_dl.appendleft(c_guid)
                    break

                self.active_tasks += 1
            else:
                t0 = time.perf_counter()
                with task_cond:
                    task_cond.wait(timeout=1.0)
                st.add('wait_tasks', time.perf_counter() - t0)
                continue

            if no_shm:
                t0 = time.perf_counter()
                with shm_cond:
                    shm_cond.wait(timeout=1.0)
                st.add('wait_shm', time.perf_counter() - t0)
                st.inc('shm_stalls')
        st.maybe_emit(force=True)


_manager.DLWorker = IDLWorker
_manager.FileWorker = IFileWorker
