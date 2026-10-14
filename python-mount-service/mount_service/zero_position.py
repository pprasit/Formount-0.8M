"""Verified Platinum immediate homing with persistent absolute-sensor offset.

Local EAS II reference: P_PX (immediate homing, volatile), P_S1…S8
indices 17/18 (absolute offset, nonvolatile), and P_SV. Never flash a
volatile PX-only change. Firmware must expose the homing offset in S[].
"""
import math

from .domain import AXIS_MAP, DriveError
from .azimuth_position import AzimuthPosition


class ZeroPosition:
    def __init__(self, hardware):
        self.hardware = hardware

    def inspect(self, axis_id, drive, axis):
        prefix = f'AX{axis.number}.'
        def number(command):
            value = float(drive._query_sync(command))
            if not math.isfinite(value):
                raise DriveError('Invalid drive readback')
            return value
        if axis_id not in AXIS_MAP:
            raise DriveError('Zero position is available for mount axes only')
        if any(drive.configuration_errors.values()):
            raise DriveError('Reconnect and verify uncertain drive configuration first')
        if any(number(f'AX{n}.MO') != 0 for n in range(1, 5)):
            raise DriveError('Disable all four motors on this drive before setting zero')
        if any(drive.commands.values()) or any(v is not None for v in drive.targets.values()):
            raise DriveError('Stop all motion before setting zero')
        resolution = drive.resolutions.get(axis_id, axis.counts_per_rev)
        if abs(number(prefix+'VX')) > max(1, resolution * .05 / 360):
            raise DriveError('Axis is moving; wait until it is stationary')
        socket = int(number(prefix+'SA[1]'))
        if socket not in range(1, 9):
            raise DriveError('Zero requires the main absolute sensor signal')
        # Socket offsets are drive-wide. Reject feedback shared with another axis.
        if any(int(number(f'AX{n}.SA[1]')) == socket for n in range(1, 5) if n != axis.number):
            raise DriveError('Position sensor is shared with another axis; zero is unavailable')
        kind = int(number(f'S{socket}[1]'))
        if kind not in (5, 6, 9, 18):
            raise DriveError('Zero supports absolute BiSS, Panasonic, EnDAT and SSI sensors only')
        return dict(target=self.hardware.binding(axis_id), socket=socket, sensor_type=kind,
                    position=number(prefix+'PX'), feedback=number(f'FP[{socket}]'),
                    offset_low=int(number(f'S{socket}[17]')),
                    offset_high=int(number(f'S{socket}[18]')))

    async def preview(self, axis_id):
        async with self.hardware.topology.operation_lock:
            async with self.hardware.mount._lock:
                drive, axis = self.hardware.drive(axis_id)
                return await drive._job(lambda: self.inspect(axis_id, drive, axis))

    async def apply(self, axis_id, expected):
        async with self.hardware.topology.operation_lock:
            async with self.hardware.mount._lock:
                self.hardware.verify_binding(axis_id, expected['target'])
                drive, axis = self.hardware.drive(axis_id)
                def write():
                    before = self.inspect(axis_id, drive, axis)
                    tolerance = max(1, drive.resolutions.get(axis_id, axis.counts_per_rev) * .005 / 360)
                    if any(before[key] != expected[key] for key in before if key not in ('position', 'feedback')) or any(abs(before[key]-expected[key]) > tolerance for key in ('position', 'feedback')):
                        raise DriveError('Position or encoder settings changed; open Zero Position again')
                    prefix = f'AX{axis.number}.'
                    socket = before['socket']
                    self.hardware.audit(dict(axis=axis_id, command='zero', before=before, status='requested'))
                    for role in AXIS_MAP:
                        drive.configuration_errors[role] = 'Zero reference change uncertain; reconnect and verify before motion'
                    try:
                        # The drive computes the sensor homing offset, including its
                        # direction/scaling. Do not guess S[17/18] from displayed degrees.
                        drive._query_sync(prefix+'PX=0')
                        px = float(drive._query_sync(prefix+'PX'))
                        fp = float(drive._query_sync(f'FP[{socket}]'))
                        offsets = tuple(int(float(drive._query_sync(f'S{socket}[{i}]'))) for i in (17, 18))
                        old_offsets = (before['offset_low'], before['offset_high'])
                        if not math.isfinite(px) or not math.isfinite(fp) or abs(px) > tolerance or abs(fp) > tolerance:
                            raise DriveError('Drive did not verify zero position; Flash was not written')
                        if abs(before['feedback']) > tolerance and offsets == old_offsets:
                            # This is a known volatile-only change, not an unknown
                            # Flash result. Restore the old reference while still
                            # disabled, verify it, then refresh the backend caches.
                            drive._query_sync(prefix+'PX='+format(before['position'], '.17g'))
                            restored = float(drive._query_sync(prefix+'PX'))
                            restored_fp = float(drive._query_sync(f'FP[{socket}]'))
                            restored_offsets = tuple(int(float(drive._query_sync(f'S{socket}[{i}]'))) for i in (17,18))
                            if not all(math.isfinite(v) for v in (restored,restored_fp)) or abs(restored-before['position']) > tolerance or abs(restored_fp-before['feedback']) > tolerance or restored_offsets != old_offsets:
                                raise DriveError('Zero rollback could not be verified; motion remains locked')
                            self.hardware.refresh_sync(drive)
                            for role in AXIS_MAP:drive.configuration_errors.pop(role,None)
                            raise DriveError('Firmware changed only volatile position. Original reference restored and verified; Flash was not written')
                        response = drive._query_sync('SV', timeout_seconds=6)
                        after_offsets = tuple(int(float(drive._query_sync(f'S{socket}[{i}]'))) for i in (17, 18))
                        final_position = float(drive._query_sync(prefix+'PX'))
                        if after_offsets != offsets or not math.isfinite(final_position) or abs(final_position) > tolerance:
                            raise DriveError('Zero readback changed after Flash; reconnect and verify')
                        self.hardware.refresh_sync(drive, after_flash=True)
                    except Exception as exc:
                        self.hardware.audit(dict(axis=axis_id, command='zero', status='failed', error=str(exc)))
                        # Leave motion inhibited after an uncertain write/Flash.
                        raise
                    for role in AXIS_MAP:
                        drive.configuration_errors.pop(role, None)
                    owner = getattr(drive, 'owner', None)
                    if owner:
                        owner.latest.pop(axis_id, None)
                        if axis_id == 'azimuth':
                            owner.azimuth_position = AzimuthPosition()
                    self.hardware.audit(dict(axis=axis_id, command='zero', offsets=offsets, status='acknowledged'))
                    return dict(saved_to_flash=True, position=0, response=response)
                return await drive._job(write, timeout_seconds=16)
