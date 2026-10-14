namespace NARIT.Mount.API.Models
{
    /// <summary>
    /// Osculating orbital elements for asteroid tracking.
    /// The angles are expected in degrees and the epoch in Julian Date.
    /// </summary>
    public class AsteroidOrbitalElements
    {
        /// <summary>Object name or designation.</summary>
        public string Name { get; set; } = "";

        /// <summary>Epoch of the orbital elements in Julian Date.</summary>
        public double Epoch_JD { get; set; }

        /// <summary>Semi-major axis in AU.</summary>
        public double SemiMajorAxis_AU { get; set; }

        /// <summary>Orbital eccentricity.</summary>
        public double Eccentricity { get; set; }

        /// <summary>Inclination in degrees.</summary>
        public double Inclination_Deg { get; set; }

        /// <summary>Longitude of ascending node in degrees.</summary>
        public double LongAscendingNode_Deg { get; set; }

        /// <summary>Argument of perihelion in degrees.</summary>
        public double ArgPerihelion_Deg { get; set; }

        /// <summary>Mean anomaly at epoch in degrees.</summary>
        public double MeanAnomaly_Deg { get; set; }
    }

    /// <summary>
    /// Osculating orbital elements for comet tracking.
    /// This API currently supports elliptical comet orbits only (eccentricity less than 1).
    /// </summary>
    public class CometOrbitalElements
    {
        /// <summary>Object name or designation.</summary>
        public string Name { get; set; } = "";

        /// <summary>Time of perihelion passage in Julian Date.</summary>
        public double PerihelionEpoch_JD { get; set; }

        /// <summary>Perihelion distance in AU.</summary>
        public double PerihelionDistance_AU { get; set; }

        /// <summary>Orbital eccentricity.</summary>
        public double Eccentricity { get; set; }

        /// <summary>Inclination in degrees.</summary>
        public double Inclination_Deg { get; set; }

        /// <summary>Longitude of ascending node in degrees.</summary>
        public double LongAscendingNode_Deg { get; set; }

        /// <summary>Argument of perihelion in degrees.</summary>
        public double ArgPerihelion_Deg { get; set; }
    }
}
