"""Load installed vendor SDKs with the controller process's architecture."""
import ctypes as c
import os
from pathlib import Path

from .astronomy import ROOT

_dll_directories = []


def sdk_path(name):
    vendor = ('ZWO' if name == 'ASICamera2.dll' else 'FLI' if name == 'libfli64.dll' else
              'ANDOR2' if name.lower().startswith('atmcd') else 'ANDOR3')
    override = os.environ.get('NARIT_' + vendor + '_SDK_DIR')
    directories = [Path(override)] if override else []
    directories += [ROOT / 'Narit-Mount-Lib/dll',
                    ROOT / 'Narit-Mount-Lib/bin/Debug/net10.0-windows']
    if vendor == 'ZWO':
        directories += [ROOT / 'temp-camera-build/runtimes' /
                        ('win-x64' if c.sizeof(c.c_void_p) == 8 else 'win-x86') / 'native']
    if vendor.startswith('ANDOR'):
        program_files = Path(os.environ.get('ProgramFiles', 'C:/Program Files'))
        directories += [program_files / 'Andor SDK3', program_files / 'Andor SDK',
                        program_files / 'Andor SOLIS', program_files / 'Andor']
    return next((folder / name for folder in directories if (folder / name).is_file()), None)


def load_sdk(name, stdcall=False):
    path = sdk_path(name)
    if not path:
        raise ValueError(f'{name} not found; install the matching camera driver/SDK on the controller')
    if hasattr(os, 'add_dll_directory'):
        _dll_directories.append(os.add_dll_directory(str(path.parent)))
    loader = c.WinDLL if stdcall and os.name == 'nt' else c.CDLL
    try:
        return loader(str(path))
    except OSError as exc:
        raise ValueError(f'Cannot load {path.name}: check SDK bitness, USB driver and runtime dependencies ({exc})') from exc


def bind(dll, name, types, result=c.c_int):
    fn = getattr(dll, name)
    fn.argtypes, fn.restype = types, result
    return fn
