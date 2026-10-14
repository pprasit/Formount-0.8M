"""Re-read native RAM after Flash; no drive reset or motor commands."""
import math
from .domain import AXES, AXIS_MAP, DriveError


def refresh_after_flash(drive, bindings=None):
    try:return refresh_configuration(drive, bindings)
    except Exception as exc:
        raise DriveError(f'Flash acknowledged, but backend refresh failed; motion remains locked: {exc}') from exc


def refresh_configuration(drive, bindings=None):
    bindings = bindings or {axis.id: axis.number for axis in AXES}
    previous_errors = dict(drive.configuration_errors)
    for role in bindings:
        drive.configuration_errors.setdefault(role, 'Saved configuration refresh incomplete; verify drive before motion')
    def number(command):
        value = float(drive._query_sync(command))
        if not math.isfinite(value):raise DriveError('Invalid configuration readback: '+command)
        return value
    scaling, modes, limits, velocities = {}, {}, {}, {}
    for role, channel in bindings.items():
        prefix = f'AX{channel}.'
        if number(prefix+'MO') != 0:raise DriveError('Disable motors before refreshing saved configuration')
        factors = [1. if i in (3,4) else number(prefix+f'FC[{i}]') for i in range(1,13)]
        if any(v < 1 for v in factors):raise DriveError('Invalid scaling after save: '+role)
        mode = number(prefix+'UM')
        if mode not in (0,1,2,3,5,6,7):raise DriveError('Invalid control mode after save: '+role)
        lower, upper = (number(prefix+key) for key in ('VL[3]','VH[3]'))
        if lower >= upper:raise DriveError('Invalid position limits after save: '+role)
        number(prefix+'PX')
        scaling[role], modes[role], limits[role] = factors, int(mode), (lower,upper)
        if role in AXIS_MAP:
            factor=(factors[6]/factors[7])*(factors[5]/factors[4])*(factors[1]/factors[0])*factors[8]/factors[9]
            ceiling=number(prefix+'VH[2]')
            if ceiling<=0:raise DriveError('Invalid velocity limit after save: '+role)
            velocities[role]=ceiling/(drive.resolutions.get(role,AXIS_MAP[role].counts_per_rev)/360*factor)
    # Publish only after every role has passed validation. Never clear an
    # uncertainty marker merely because a serial reconnection succeeded.
    drive.scaling.update(scaling)
    drive.unit_modes.update(modes)
    drive.position_limits.update(limits)
    drive.velocity_limits.update(velocities)
    for role in bindings:
        if role not in previous_errors:drive.configuration_errors.pop(role,None)
    owner = getattr(drive, 'owner', drive)
    if owner is not None:
        owner.latest.clear()
        if hasattr(owner, '_frame_lock'):
            with owner._frame_lock:owner._frames.clear()
    return {'configuration_refreshed': True}
