import pytest
from test_camera_sdk import SDK, put
from mount_service.camera_andor import Andor2
from mount_service.api import CameraCooling
from pydantic import ValidationError


def adapter():
    camera = Andor2()
    camera.handle = 100
    camera.dll = SDK(default=20002,
        GetTemperatureRange=lambda lo, hi: (put(lo, -100), put(hi, 20), 20002)[-1],
        IsCoolerOn=lambda p: (put(p, 1), 20002)[-1],
        GetTemperatureF=lambda p: (put(p, -25.5), 20036)[-1],
        GetTemperatureStatus=lambda *args: 20066)
    return camera


def test_stabilized_code_is_temperature_success():
    state = adapter().cooling_status()
    assert state['temperature_c'] == -25.5
    assert state['state'] == 'Stabilized'
    assert state['enabled'] is True
    assert state['target_c'] is None


def test_invalid_target_does_not_change_cooler():
    camera = adapter()
    with pytest.raises(ValueError):
        camera.set_cooling(True, -101)
    assert not any(name in ('SetTemperature', 'CoolerON') for name, _ in camera.dll.calls)


def test_setpoint_precedes_enabling_and_is_retained():
    camera = adapter()
    assert camera.set_cooling(True, -30)['target_c'] == -30
    calls = [name for name, _ in camera.dll.calls]
    assert calls.index('SetTemperature') < calls.index('CoolerON')


def test_failed_temperature_read_is_not_reported_as_zero():
    camera = adapter()
    camera.dll.functions['GetTemperatureF'] = lambda p: 20072
    with pytest.raises(ValueError, match='20072'):
        camera.cooling_status()


@pytest.mark.parametrize('value', [-20.5, '0', True])
def test_target_requires_integer_degrees(value):
    with pytest.raises(ValidationError):
        CameraCooling(target_c=value)
def test_reserved_temperature_status_cannot_overwrite_commanded_target():
    camera = adapter()
    camera.dll.functions['GetTemperatureStatus'] = lambda sensor, target, ambient, volts: (put(target, -10.453), 20002)[-1]
    state = camera.set_cooling(True, -10)
    assert state['target_c'] == -10
    assert not any(name == 'GetTemperatureStatus' for name, _ in camera.dll.calls)
