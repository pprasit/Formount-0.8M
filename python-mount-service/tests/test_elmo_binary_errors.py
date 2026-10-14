import asyncio
import pytest
from test_live_control import fake_drive
from mount_service.domain import AXIS_MAP, DriveError
from mount_service.transports import parse_reply, ElmoCommandRejected


@pytest.mark.parametrize('code',[28,166,220,222])
def test_documented_binary_error_is_rejection_not_corrupt_ascii(code):
    with pytest.raises(ElmoCommandRejected) as result:
        parse_reply('AX3.BG',b'AX3.BG\r'+bytes([code])+b'?;')
    assert result.value.code==code
    assert result.value.command=='AX3.BG'
    assert 'Non-ASCII' not in str(result.value)


def test_non_error_binary_data_still_reports_communication_problem():
    with pytest.raises(DriveError,match='Non-ASCII'):
        parse_reply('AX3.PX',b'AX3.PX\r\xff;')


@pytest.mark.parametrize('direction',[-1,1])
@pytest.mark.parametrize('code',[220,222])
def test_rejected_jog_reports_boundary_only_after_fresh_position_readback(direction,code):
    axis=AXIS_MAP['rotator']
    drive,commands=fake_drive(axis)
    drive.position_limits['rotator']=(axis.deg_to_counts(-300),axis.deg_to_counts(300))
    def query(command):
        commands.append(command)
        if command=='AX3.BG':raise ElmoCommandRejected(command,code)
        if command=='AX3.PX':return str(axis.deg_to_counts(direction*300))
        if command=='AX3.VH[2]':return str(drive.velocity_units(axis,50))
        return '0'
    drive._query_sync=query
    with pytest.raises(DriveError,match='Lower limit reached' if direction<0 else 'Upper limit reached'):
        asyncio.run(drive.velocity(axis,direction*10,.8))
    assert 'AX3.ST' in commands and 'AX3.PX' in commands
    assert drive.commands['rotator']==0
    assert 'Motion stopped' in drive.motion_notices['rotator']


def test_limit_rejection_away_from_boundary_keeps_actual_drive_error():
    axis=AXIS_MAP['rotator']
    drive,commands=fake_drive(axis)
    drive.position_limits['rotator']=(axis.deg_to_counts(-300),axis.deg_to_counts(300))
    def query(command):
        if command=='AX3.BG':raise ElmoCommandRejected(command,222)
        return str(drive.velocity_units(axis,50)) if command=='AX3.VH[2]' else '0'
    drive._query_sync=query
    with pytest.raises(ElmoCommandRejected):asyncio.run(drive.velocity(axis,10,.8))
    assert not drive.motion_notices
