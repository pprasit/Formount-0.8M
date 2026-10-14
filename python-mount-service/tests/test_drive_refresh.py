import pytest
from test_mapped_hardware import setup
from mount_service.drive_refresh import refresh_configuration, refresh_after_flash
from mount_service.domain import DriveError


def test_refresh_publishes_native_scaling_modes_limits_and_discards_stale_frames(tmp_path):
    topology, _ = setup(tmp_path)
    drive = topology.mount.transport
    drive._port.values.update({'AX3.FC[2]':2,'AX3.UM':2,'AX3.VL[3]':-300,'AX3.VH[3]':400})
    drive.latest['rotator'] = 'stale'
    drive._frames.append('stale')
    assert refresh_after_flash(drive)['configuration_refreshed']
    assert drive.scaling['rotator'][1] == 2
    assert drive.unit_modes['rotator'] == 2
    assert drive.position_limits['rotator'] == (-300,400)
    assert not drive.latest and not drive._frames and not drive.configuration_errors
    assert not any('=' in c or c == 'RS' or c.endswith('.BG') for c in drive._port.commands)


def test_incomplete_refresh_keeps_motion_locked_and_does_not_publish_partial_cache(tmp_path):
    topology, _ = setup(tmp_path)
    drive = topology.mount.transport
    drive.scaling['azimuth'] = [7]*12
    drive._port.values['AX4.FC[2]'] = float('nan')
    with pytest.raises(DriveError, match='Flash acknowledged'):refresh_after_flash(drive)
    assert drive.scaling['azimuth'] == [7]*12
    assert len(drive.configuration_errors) == 4


def test_refresh_does_not_erase_unresolved_write_error(tmp_path):
    topology, _ = setup(tmp_path)
    drive = topology.mount.transport
    drive.configuration_errors['rotator'] = 'Unknown write outcome'
    refresh_configuration(drive)
    assert drive.configuration_errors == {'rotator':'Unknown write outcome'}
