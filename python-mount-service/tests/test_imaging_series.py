from astropy.io import fits
import numpy as np
from mount_service.image_archive import ImageArchive

def test_series_survives_reload_and_new_series_preserves_old_images(tmp_path):
    folder=tmp_path/'captures';folder.mkdir()
    archive=ImageArchive(tmp_path)
    for name in ['one.fits','two.fits']:
        fits.writeto(folder/name,np.ones((8,8),dtype=np.uint16))
        archive.ensure(name);archive.add_to_series(name)
    series=archive.current_series()
    assert series['count']==2
    assert [r['filename'] for r in series['images']]==['two.fits','one.fits']
    assert ImageArchive(tmp_path).current_series()['id']==series['id']
    new=archive.new_series("Test series");assert new['name']=='Test series'
    assert new['count']==0 and new['id']!=series['id']
    assert archive.get('one.fits') and archive.get('two.fits')
    assert ImageArchive(tmp_path).current_series()['count']==0
    fits.writeto(folder/'three.fits',np.ones((8,8),dtype=np.uint16))
    archive.ensure('three.fits');archive.add_to_series('three.fits')
    assert archive.current_series()['count']==1
    assert ImageArchive(tmp_path).current_series()['name']=='Test series'
    assert ImageArchive(tmp_path).current_series()['images'][0]['filename']=='three.fits'


def test_duplicate_series_requires_new_name(tmp_path):
    import pytest
    from mount_service.image_archive import SeriesExistsError
    archive=ImageArchive(tmp_path)
    first=archive.new_series('M42 Night')
    for name in ['M42 Night',' m42 night ']:
        with pytest.raises(SeriesExistsError,match='already exists'):
            archive.new_series(name)
        assert archive.current_series()['id']==first['id']
    assert archive.new_series('M42 Night 2')['id']!=first['id']
