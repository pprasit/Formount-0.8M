"""Common server-clock trajectory source for every astronomical target."""
import math
import astropy.units as u


class SetPointGenerator:
    interval_s = .1

    def __init__(self, clock, preview):
        self.clock = clock
        self.preview = preview

    async def generate(self, object_spec):
        # Never use a browser timestamp to advance a live tracking trajectory.
        now = self.clock()
        payload = dict(object_spec)
        payload.pop('utc', None)
        target = await self.preview({**payload, 'utc': now.utc.isot})
        future = await self.preview({**payload, 'utc': (now+self.interval_s*u.s).utc.isot})
        if not target['slew_allowed'] or not future['slew_allowed']:
            raise ValueError('Target is below the horizon or outside drive altitude limits')
        rates = {
            'azimuth': ((future['encoder_azimuth_deg']-target['encoder_azimuth_deg']+180)%360-180)/self.interval_s,
            'altitude': (future['encoder_altitude_deg']-target['encoder_altitude_deg'])/self.interval_s,
        }
        if not all(math.isfinite(v) for v in rates.values()):
            raise ValueError('Tracking rate must be finite')
        kind = 'satellite' if payload.get('tle') else 'small_body' if payload.get('orbit') else 'solar_system' if payload.get('body') else 'celestial'
        target['set_point_generator'] = {'object_type': kind, 'clock': 'server_utc',
                                         'interval_ms': 100, 'generated_utc': now.utc.isot}
        return target, rates
