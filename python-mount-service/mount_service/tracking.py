"""Position-error feedback plus target-rate feedforward, matching C# SiderealTracking."""
import math

class TrackingServo:
    def __init__(self, maximum_velocity, kp=1.5, ki=0.0, kd=0.3):
        self.maximum_velocity=maximum_velocity
        self.kp,self.ki,self.kd=kp,ki,kd
        self.integral=0.0

    def velocity(self,target,position,target_rate,actual_rate,dt):
        if not all(math.isfinite(v) for v in (target,position,target_rate,actual_rate,dt)):
            raise ValueError('Tracking needs finite target and telemetry')
        error=target-position
        proposed=max(-1.0,min(1.0,self.integral+error*max(0,min(dt,1))))
        command=self.kp*error+self.ki*proposed+self.kd*(target_rate-actual_rate)+target_rate
        limited=max(-self.maximum_velocity,min(self.maximum_velocity,command))
        if command==limited or error*command<0:self.integral=proposed
        return limited


def tracking_velocity_status(active, target, axes, tolerance):
    rates=(target or {}).get('rates_deg_s',{})
    by_id={axis['id']:axis for axis in axes}
    errors={}
    for name in ('azimuth','altitude'):
        axis=by_id.get(name,{})
        actual=axis.get('velocity_deg_s');expected=rates.get(name)
        ready=active and axis.get('valid') and axis.get('enabled') and all(isinstance(v,(int,float)) and math.isfinite(v) for v in (actual,expected))
        errors[name]=(actual-expected)*3600 if ready else None
    state='idle'
    if active:
        state='unavailable' if any(v is None for v in errors.values()) else 'tracking' if all(abs(v)<=tolerance for v in errors.values()) else 'acquiring'
    return {'state':state,'error_arcsec_s':errors,'tolerance_arcsec_s':tolerance}
