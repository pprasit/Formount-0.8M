import io
import numpy as np
from astropy.io import fits
from PIL import Image
from mount_service.image_archive import ImageArchive


def test_compressed_detailed_preview_preserves_fits_and_is_larger_than_thumbnail(tmp_path):
    directory=tmp_path;captures=directory/'captures';captures.mkdir()
    raw=np.full((1800,3000),1000,dtype=np.uint16);raw[800:820,1200:1220]=50000
    path=captures/'test.fits';fits.writeto(path,raw);original=path.read_bytes()
    archive=ImageArchive(directory);record=archive.ensure('test.fits')
    quick,qw,qh=archive.display_webp('test.fits',640)
    detail,dw,dh=archive.display_webp('test.fits',2560,black=900,white=50000,tone=5)
    assert qw<=640 and dw<=2560 and dw>record['preview_width']
    assert Image.open(io.BytesIO(detail)).format=='WEBP'
    assert Image.open(io.BytesIO(quick)).size==(qw,qh)
    assert len(detail)<raw.nbytes/10
    assert path.read_bytes()==original
    levels=archive.display_levels('test.fits')['percentiles']
    assert len(levels)==1001 and levels==sorted(levels) and levels[-1]==50000

    packed=np.asarray(Image.open(io.BytesIO(archive.display_data('test.fits',2560))))
    recovered=(packed[:,:,0].astype(np.uint16)<<8)|packed[:,:,1]
    np.testing.assert_array_equal(recovered,archive.display_sensor('test.fits',2560).astype(np.uint16))
