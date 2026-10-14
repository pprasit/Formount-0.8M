"""CPU-heavy video work runs outside the hardware/telemetry process."""
import io
import math
import os
import numpy as np
from .camera_preview import stretched_preview
_grid=None
_rng=np.random.default_rng(42)
def lower_priority():
    if os.name=='nt':
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(),0x4000)
def encode_preview(raw):
    preview,_=stretched_preview(raw,maximum=960)
    buffer=io.BytesIO();preview.save(buffer,format='JPEG',quality=80)
    maximum=np.iinfo(raw.dtype).max
    return buffer.getvalue(),float(raw.max())/maximum*100,float(np.count_nonzero(raw>=maximum))/raw.size*100
def simulated_frame(h,w,count,gain,exposure_s,fmt):
    global _grid
    if _grid is None or _grid[0].shape!=(h,w):_grid=np.mgrid[:h,:w].astype(np.float32)
    yy,xx=_grid
    cx=w*(.5+.12*math.sin(count/40));cy=h*(.5+.1*math.cos(count/55));radius=max(4,min(w,h)*.16)
    disk=np.maximum(0,1-((xx-cx)**2+(yy-cy)**2)/radius**2)
    signal=700+disk*(15000+6000*np.sin(yy/max(1,radius)*5+count/50))*(1+gain/600)*min(2,exposure_s/.033)
    raw=np.clip(signal+_rng.normal(0,60,(h,w)),0,65535)
    return (raw/256 if fmt==0 else raw).astype(np.uint8 if fmt==0 else np.uint16)
