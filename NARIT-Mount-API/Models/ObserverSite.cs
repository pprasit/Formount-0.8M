namespace NARIT.Mount.API.Models
{
    /// <summary>
    /// Observer site location and timezone configuration.
    /// Required for astronomical coordinate calculations (RA/Dec ? Az/Alt).
    /// </summary>
    public class ObserverSite
    {
        /// <summary>Latitude in degrees (-90 to +90, North positive)</summary>
        public double Latitude { get; set; }

        /// <summary>Longitude in degrees (-180 to +180, East positive)</summary>
        public double Longitude { get; set; }

        /// <summary>Elevation above sea level in meters</summary>
        public double Elevation { get; set; }

        /// <summary>UTC offset in hours (e.g., +7 for Thailand)</summary>
        public double TimezoneUtcOffset { get; set; }

        /// <summary>
        /// Create observer site with specified coordinates.
        /// </summary>
        /// <param name="latitude">Latitude in degrees (-90 to +90)</param>
        /// <param name="longitude">Longitude in degrees (-180 to +180)</param>
        /// <param name="elevation">Elevation in meters above sea level</param>
        /// <param name="timezoneUtcOffset">UTC offset in hours (default: +7 for Thailand)</param>
        public ObserverSite(double latitude, double longitude, double elevation, double timezoneUtcOffset = 7.0)
        {
            Latitude = latitude;
            Longitude = longitude;
            Elevation = elevation;
            TimezoneUtcOffset = timezoneUtcOffset;
        }

        /// <summary>
        /// Default NARIT observatory (Doi Inthanon, Thailand).
        /// Lat: 18.5736°N, Lon: 98.4820°E, Elev: 2457m, UTC+7
        /// </summary>
        public static ObserverSite NARIT_DoiInthanon => new ObserverSite(18.5736, 98.4820, 2457, 7.0);
    }
}
