"""Read this Windows user's DPAPI-protected NARIT key without subprocess output."""
import ctypes
import os
from pathlib import Path

def credential():
    value=os.environ.get('NARIT_LLLM_API_KEY')
    if value:return value
    if os.name!='nt':return None
    path=Path(os.environ.get('LOCALAPPDATA',''))/'NARIT-MountController'/'lllm-key.dpapi'
    if not path.is_file():return None
    try:
        encrypted=bytes.fromhex(path.read_text(encoding='ascii').strip())
        from ctypes import wintypes
        class Blob(ctypes.Structure):
            _fields_=[('length',wintypes.DWORD),('data',ctypes.POINTER(ctypes.c_ubyte))]
        data=(ctypes.c_ubyte*len(encrypted)).from_buffer_copy(encrypted)
        source=Blob(len(encrypted),data);result=Blob()
        unprotect=ctypes.windll.crypt32.CryptUnprotectData
        unprotect.argtypes=[ctypes.POINTER(Blob),ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(Blob)]
        unprotect.restype=wintypes.BOOL
        if not unprotect(ctypes.byref(source),None,None,None,None,1,ctypes.byref(result)):return None
        try:return ctypes.string_at(result.data,result.length).decode('utf-16-le')
        finally:
            ctypes.memset(result.data,0,result.length)
            ctypes.windll.kernel32.LocalFree.argtypes=[ctypes.c_void_p]
            ctypes.windll.kernel32.LocalFree(result.data)
    except (ValueError,OSError,UnicodeError):return None
