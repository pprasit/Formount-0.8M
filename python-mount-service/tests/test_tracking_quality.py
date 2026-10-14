import pytest
from mount_service.tracking import tracking_velocity_status


def quality(actual=(.001,.002),rates=(.001,.002),tolerance=1,active=True,valid=True):
    axes=[{'id':name,'velocity_deg_s':value,'valid':valid,'enabled':True} for name,value in zip(('azimuth','altitude'),actual)]
    target={'rates_deg_s':dict(zip(('azimuth','altitude'),rates))}
    return tracking_velocity_status(active,target,axes,tolerance)


def test_velocity_error_uses_arcseconds_and_target_rate():
    result=quality(actual=(.002,.001))
    assert result['error_arcsec_s']==pytest.approx({'azimuth':3.6,'altitude':-3.6})
    assert result['state']=='acquiring'
    assert quality(actual=(.002,.001),tolerance=4)['state']=='tracking'


def test_both_axes_must_be_within_tolerance():
    assert quality(actual=(.001,.003))['state']=='acquiring'
    assert quality()['state']=='tracking'


@pytest.mark.parametrize('options,state',[({'active':False},'idle'),({'valid':False},'unavailable'),({'actual':(float('nan'),.002)},'unavailable')])
def test_invalid_or_inactive_tracking_has_no_false_lock(options,state):
    result=quality(**options)
    assert result['state']==state
    assert result['error_arcsec_s']['azimuth'] is None
