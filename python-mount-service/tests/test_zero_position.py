import asyncio
import pytest
from test_mapped_hardware import setup
from mount_service.zero_position import ZeroPosition
from mount_service.domain import DriveError


def fixture(tmp_path, persistent=True):
    topology, hardware = setup(tmp_path)
    hardware.audit = lambda record: None
    port = topology.mount.transport._port
    port.values.update({'AX3.SA[1]':3, 'S3[1]':18, 'AX3.PX':200000, 'FP[3]':200000, 'S3[17]':100, 'S3[18]':0})
    write = port.write
    def homing(data):
        write(data)
        if port.command == 'AX3.PX=0':
            port.values['FP[3]'] = 0
            if persistent:port.values['S3[17]'] = -199900
        elif port.command.startswith('AX3.PX='):
            port.values['FP[3]'] = port.values['AX3.PX']
    port.write = homing
    return topology, port, ZeroPosition(hardware)


def run(zero, expected=None):
    async def transaction():
        preview = await zero.preview('rotator') if expected is None else expected
        return await zero.apply('rotator', preview)
    return asyncio.run(transaction())


def test_persistent_offset_verified_before_flash(tmp_path):
    topology, port, zero = fixture(tmp_path)
    assert run(zero)['saved_to_flash'] is True
    assert port.commands.index('AX3.PX=0') < port.commands.index('SV')
    assert not topology.mount.transport.configuration_errors
    assert not any('.BG' in command or '.MO=' in command for command in port.commands)


def test_volatile_only_homing_never_flashes_or_reports_success(tmp_path):
    topology, port, zero = fixture(tmp_path, persistent=False)
    with pytest.raises(DriveError, match='Original reference restored'):run(zero)
    assert 'SV' not in port.commands
    assert not topology.mount.transport.configuration_errors
    assert port.values['AX3.PX'] == 200000


@pytest.mark.parametrize('field,value', [('AX1.MO',1),('AX3.VX',1e8),('S3[1]',1),('AX2.SA[1]',3)])
def test_invalid_preconditions_never_write(tmp_path, field, value):
    _, port, zero = fixture(tmp_path)
    port.values[field] = value
    with pytest.raises(DriveError):run(zero)
    assert not any('=' in command or command == 'SV' for command in port.commands)


def test_changed_position_rejected_without_writes(tmp_path):
    _, port, zero = fixture(tmp_path)
    expected = asyncio.run(zero.preview('rotator'))
    port.values['FP[3]'] += 100000
    with pytest.raises(DriveError, match='changed'):run(zero, expected)
    assert 'AX3.PX=0' not in port.commands


def test_flash_failure_keeps_motion_inhibited(tmp_path):
    topology, port, zero = fixture(tmp_path)
    original = zero.hardware.drive
    def drive_for(role):
        drive, axis = original(role)
        query = drive._query_sync
        def fail(command, **kwargs):
            if command == 'SV':raise DriveError('Flash rejected')
            return query(command, **kwargs)
        drive._query_sync = fail
        return drive, axis
    zero.hardware.drive = drive_for
    with pytest.raises(DriveError, match='Flash rejected'):run(zero)
    assert topology.mount.transport.configuration_errors
