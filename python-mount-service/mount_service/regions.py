"""Mechanical travel regions: preserve turns instead of inferring them from sky azimuth."""
import time
from .domain import AXIS_MAP


def travel_region(azimuth, altitude, inputs=None):
    az=AXIS_MAP['azimuth'];alt=AXIS_MAP['altitude']
    valid=azimuth is not None and altitude is not None
    in_az=valid and az.minimum_deg<=azimuth<=az.maximum_deg
    in_alt=valid and alt.minimum_deg<=altitude<=alt.maximum_deg
    turn=int(azimuth//360) if valid else None
    return {'turn':turn,'name':f'Turn {turn+1}' if valid else 'Unknown',
            'azimuth_in_range':bool(in_az),'altitude_in_range':bool(in_alt),
            'azimuth_limits':[az.minimum_deg,az.maximum_deg],
            'altitude_limits':[alt.minimum_deg,alt.maximum_deg],
            'reason':'within travel' if in_az and in_alt else 'outside configured travel' if valid else 'telemetry unavailable',
            'source':'mechanical encoder turns; sensor wiring not yet verified'}


def input_snapshot(values, stamp, error=None):
    age=(time.perf_counter()-stamp)*1000 if stamp else None
    valid=bool(stamp and age<1000 and not error)
    raw=values.get('1')
    return {'valid':valid,'age_ms':round(age,1) if age is not None else None,'error':error,
            'raw':values,'active_bits':{k:[b for b in range(32) if v&(1<<b)] for k,v in values.items()},
            'azimuth':{'command':'IP[1]','raw':raw,
                       'cw':bool(raw&(1<<16)) if valid and raw is not None else None,
                       'ccw':bool(raw&(1<<17)) if valid and raw is not None else None,
                       'cw_bit':16,'ccw_bit':17,'mapping_verified':False,
                       'mapping_source':'legacy AzimuthManager; confirm physical wiring'}}
