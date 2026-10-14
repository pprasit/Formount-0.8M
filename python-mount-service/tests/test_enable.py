import asyncio
import time
import pytest
from mount_service.domain import AXES, DriveError
from mount_service.transports import SerialElmoDrive

@pytest.mark.parametrize('axis', AXES)
def test_enable_waits_for_servo_before_zero_jog(axis):
    drive=SerialElmoDrive('TEST',921600)
    drive.unit_modes[axis.id]=5
    sent=[]
    ready=iter([0,1])
    def query(command):
        sent.append(command)
        if command.endswith('.MO'): return '1'
        if command.endswith('.SO'): return str(next(ready))
        if '.JP=' in command:
            assert f'AX{axis.number}.MO=1' in sent
            assert sent[-2]==f'AX{axis.number}.SO'
        return ''
    drive._query_sync=query
    async def job(fn,**kwargs): return fn()
    drive._job=job
    asyncio.run(drive.enable(axis,True))
    assert drive._motor_states[axis.id]
    assert sent[-1]==f'AX{axis.number}.JP=0'

@pytest.mark.parametrize('axis', AXES)
def test_enable_rolls_back_if_zero_jog_rejected(axis):
    drive=SerialElmoDrive('TEST',921600)
    drive.unit_modes[axis.id]=5
    sent=[]
    def query(command):
        sent.append(command)
        if '.JP=' in command: raise DriveError('rejected')
        return '1'
    drive._query_sync=query
    async def job(fn,**kwargs): return fn()
    drive._job=job
    with pytest.raises(DriveError): asyncio.run(drive.enable(axis,True))
    assert sent[-1]==f'AX{axis.number}.MO=0'
    assert not drive._motor_states[axis.id]
