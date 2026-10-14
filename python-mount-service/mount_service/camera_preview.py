"""Small display-only screen stretch; sensor/FITS values are never modified."""
import math
import numpy as np
from PIL import Image

# Ported from C# HistogramStretchProfile (black %, white %, asinh strength).
PROFILES = {'Auto':(.5,99.95,8.), 'Low':(1.,99.98,4.), 'Medium':(3.,99.95,7.),
            'High':(8.,99.90,12.), 'BrightStar':(1.5,99.97,5.), 'Moon':(.2,99.70,1.4),
            'Planet':(.05,99.99,1.2), 'DeepSky':(12.,99.85,18.)}


def preview_sensor(image, maximum=1280):
    height,width=image.shape
    factor=max(1,math.ceil(max(width,height)/maximum))
    pad_y,pad_x=(-height)%factor,(-width)%factor
    source=np.pad(image,((0,pad_y),(0,pad_x)),mode='edge') if pad_y or pad_x else image
    return source.reshape(source.shape[0]//factor,factor,source.shape[1]//factor,factor).max(axis=(1,3))


def stretched_preview(image, profile='Auto', maximum=1280):
    black_pct, white_pct, tone = PROFILES[profile]
    small=preview_sensor(image,maximum).astype(np.float32)
    low,high=np.percentile(small,[black_pct,white_pct])
    if profile=='Auto':
        median=float(np.median(small));sigma=1.4826*float(np.median(np.abs(small-median)))
        # Sparse star fields occupy less than the percentile cut: suppress sky noise
        # instead of mapping the sky background to mid-grey.
        low=max(low,median+3*sigma)
        high=max(high,low+8*max(1,sigma))
    values=np.clip((small-low)/max(1,high-low),0,1)
    values=np.arcsinh(values*tone)/np.arcsinh(tone)
    preview=Image.fromarray(np.rint(values*255).astype(np.uint8))
    return preview,dict(profile=profile,black=float(low),white=float(high),tone=tone,width=preview.width,height=preview.height)


def save_preview(image,path):
    preview,settings=stretched_preview(image)
    preview.save(path,optimize=True)
    return settings
