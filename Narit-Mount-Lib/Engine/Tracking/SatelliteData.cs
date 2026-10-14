using System;
using System.ComponentModel;
using PWLib.Astro;

namespace ForkMount_WPF.Engine.Tracking
{
    public class SatelliteTLE
    {
        public string Name { get; set; }
        public string Line1 { get; set; }
        public string Line2 { get; set; }

        public SatelliteTLE(string name, string line1, string line2)
        {
            Name = name;
            Line1 = line1;
            Line2 = line2;
        }
    }

    public class SatelliteInfo : INotifyPropertyChanged
    {
        private string _name;
        private double _azimuth;
        private double _altitude;
        private double _azimuthVelocity;
        private double _altitudeVelocity;
        private double _range;
        private bool _isVisible;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public double Azimuth
        {
            get => _azimuth;
            set { _azimuth = value; OnPropertyChanged(nameof(Azimuth)); }
        }

        public double Altitude
        {
            get => _altitude;
            set { _altitude = value; OnPropertyChanged(nameof(Altitude)); }
        }

        public double AzimuthVelocity
        {
            get => _azimuthVelocity;
            set { _azimuthVelocity = value; OnPropertyChanged(nameof(AzimuthVelocity)); }
        }

        public double AltitudeVelocity
        {
            get => _altitudeVelocity;
            set { _altitudeVelocity = value; OnPropertyChanged(nameof(AltitudeVelocity)); }
        }

        public double Range
        {
            get => _range;
            set { _range = value; OnPropertyChanged(nameof(Range)); }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ObserverLocation
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }

        public ObserverLocation(double latitude, double longitude, double altitude)
        {
            Latitude = latitude;
            Longitude = longitude;
            Altitude = altitude;
        }
    }

    public struct EciCoordinates
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double VX { get; set; }
        public double VY { get; set; }
        public double VZ { get; set; }

        public EciCoordinates(double x, double y, double z, double vx, double vy, double vz)
        {
            X = x;
            Y = y;
            Z = z;
            VX = vx;
            VY = vy;
            VZ = vz;
        }
    }

    public struct TopocentricCoordinates
    {
        public double Azimuth { get; set; }
        public double Elevation { get; set; }
        public double Range { get; set; }
        public double AzimuthRate { get; set; }
        public double ElevationRate { get; set; }
        public double RangeRate { get; set; }

        public TopocentricCoordinates(double azimuth, double elevation, double range,
                                     double azimuthRate, double elevationRate, double rangeRate)
        {
            Azimuth = azimuth;
            Elevation = elevation;
            Range = range;
            AzimuthRate = azimuthRate;
            ElevationRate = elevationRate;
            RangeRate = rangeRate;
        }
    }

    /// <summary>
    /// Satellite Tracker using PWLib.Astro.Satellite for accurate SGP4 propagation
    /// Replaces custom SGP4Calculator with proven PlaneWave implementation
    /// WARNING: PWLib.Astro.Satellite has internal state modified by RefreshPosition()
    /// Do NOT share instances between threads - use Clone() for separate tracking contexts
    /// </summary>
    public class SatelliteTracker
    {
        private SatelliteTLE _tle;
        private Satellite _satellite;  // PWLib.Astro.Satellite
        private bool _isInitialized;
        
        private TopocentricCoordinates _currentPosition;
        private TopocentricCoordinates _previousPosition;
        private double _previousTimestamp;
        private double _azimuthVelocity;
        private double _altitudeVelocity;

        public string Name => _tle.Name;
        public SatelliteTLE TLE => _tle;
        public TopocentricCoordinates CurrentPosition => _currentPosition;
        public double AzimuthVelocity => _azimuthVelocity;
        public double AltitudeVelocity => _altitudeVelocity;
        
        /// <summary>
        /// Get the underlying PWLib.Astro.Satellite object
        /// </summary>
        public Satellite PwSatellite => _satellite;

        public SatelliteTracker(SatelliteTLE tle)
        {
            _tle = tle;
            // Initialize PWLib.Astro.Satellite with TLE data
            try
            {
                _satellite = new Satellite(_tle.Name, _tle.Line1, _tle.Line2);
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                // Only log errors (not every satellite creation)
                System.Diagnostics.Debug.WriteLine($"[SATELLITE ERROR] Failed to create {_tle.Name}: {ex.Message}");
                _isInitialized = false;
            }
        }
        
        /// <summary>
        /// Create a new independent instance with the same TLE data
        /// Use this to avoid race conditions when tracking from multiple threads
        /// </summary>
        public SatelliteTracker Clone()
        {
            return new SatelliteTracker(_tle);
        }

        public bool Initialize()
        {
            // Already initialized in constructor
            return _isInitialized;
        }

        /// <summary>
        /// Get topocentric coordinates using PWLib.Astro.Satellite
        /// Follows the same pattern as NaritMountLib.Mount.Target.SatelliteMountTarget
        /// </summary>
        public TopocentricCoordinates GetTopocentric(ObserverLocation observer, DateTime time)
        {
            if (!_isInitialized || _satellite == null)
            {
                return new TopocentricCoordinates(0, -999, 0, 0, 0, 0); // Use -999 to indicate error
            }
            
            try
            {
                // Create PWLib.Astro.LatLon from ObserverLocation
                var site = new LatLon(
                    Angle.FromDegs(observer.Latitude), 
                    Angle.FromDegs(observer.Longitude));
                
                // Get Julian Date
                double jd = AstroTime.JulianDay(time);
                
                // Refresh satellite position using PWLib's SGP4 (same as SatelliteMountTarget)
                _satellite.RefreshPosition(site, observer.Altitude, jd);
                
                // Get RaDec and convert to AltAz
                var raDec = _satellite.RaDecApparent;
                var altAz = raDec.ToAltAz(site, jd);
                
                // Calculate velocity by numerical differentiation (small time step)
                double dt = 1.0 / 86400.0; // 1 second in Julian days
                _satellite.RefreshPosition(site, observer.Altitude, jd + dt);
                var raDecNext = _satellite.RaDecApparent;
                var altAzNext = raDecNext.ToAltAz(site, jd + dt);
                
                // Restore current position
                _satellite.RefreshPosition(site, observer.Altitude, jd);
                
                // Calculate angular rates (degrees per second)
                double azRate = (altAzNext.Az.Degs - altAz.Az.Degs);
                if (azRate > 180) azRate -= 360;
                if (azRate < -180) azRate += 360;
                double altRate = (altAzNext.Alt.Degs - altAz.Alt.Degs);
                
                // Estimate range (would need access to satellite's orbital elements for exact value)
                double range = 0; // PWLib doesn't expose RangeKm directly; tracking doesn't require it
                double rangeRate = 0;
                
                return new TopocentricCoordinates(
                    altAz.Az.Degs,
                    altAz.Alt.Degs,
                    range,
                    azRate,
                    altRate,
                    rangeRate
                );
            }
            catch
            { 
                return new TopocentricCoordinates(0, -999, 0, 0, 0, 0); // Use -999 to indicate error
            }
        }

        public void UpdatePositionAndVelocity(ObserverLocation observer, DateTime time, double timestamp)
        {
            if (!_isInitialized) return;

            try
            {
                _previousPosition = _currentPosition;
                _currentPosition = GetTopocentric(observer, time);

                if (_previousTimestamp > 0 && _previousPosition.Elevation != 0)
                {
                    double deltaTime = timestamp - _previousTimestamp;
                    
                    if (deltaTime > 0.001)
                    {
                        double azDiff = _currentPosition.Azimuth - _previousPosition.Azimuth;
                        if (azDiff > 180.0) azDiff -= 360.0;
                        if (azDiff < -180.0) azDiff += 360.0;
                        
                        _azimuthVelocity = azDiff / deltaTime;
                        _altitudeVelocity = (_currentPosition.Elevation - _previousPosition.Elevation) / deltaTime;
                    }
                }
                else
                {
                    _azimuthVelocity = 0.0;
                    _altitudeVelocity = 0.0;
                }

                _previousTimestamp = timestamp;
            }
            catch
            {
                _azimuthVelocity = 0.0;
                _altitudeVelocity = 0.0;
            }
        }
    }
}
