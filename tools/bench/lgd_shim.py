"""Lets Legendary 0.21.1 be imported from a source checkout (its __init__ asks importlib.metadata for the
'legendary-gl' distribution, which a checkout doesn't have). Imported first in every process, including spawned
children, because they unpickle PlainDLManager from this module."""
import importlib.metadata as _im

if not getattr(_im, '_glider_shim', False):
    _orig = _im.version
    _im.version = lambda n: '0.21.1' if n == 'legendary-gl' else _orig(n)
    _im._glider_shim = True

from legendary.downloader.mp.manager import DLManager  # noqa: E402


class PlainDLManager(DLManager):
    """Stock DLManager (or a patched fork's), only re-homed so spawned children import this shim first."""


from legendary.downloader.mp import manager as _manager  # noqa: E402
from legendary.downloader.mp.workers import DLWorker, FileWorker  # noqa: E402


class PlainDLWorker(DLWorker):
    """Stock worker, re-homed for the same reason as PlainDLManager."""


class PlainFileWorker(FileWorker):
    """Stock writer, re-homed for the same reason as PlainDLManager."""


def use_plain_workers():
    _manager.DLWorker = PlainDLWorker
    _manager.FileWorker = PlainFileWorker


use_plain_workers()  # lgd_instr re-patches with its instrumented classes after importing this module
