using NARIT.Mount.API.Models;

namespace NARIT.Mount.API.Internal
{
    internal static class SmallBodyEphemerisCalculator
    {
        private const double J2000 = 2451545.0;
        private const double GaussianGravitationalConstant = 0.01720209895; // rad/day
        private const double TwoPi = Math.PI * 2.0;
        private const double DegreesToRadians = Math.PI / 180.0;
        private const double RadiansToDegrees = 180.0 / Math.PI;

        public static SmallBodyEquatorialPosition ComputeAsteroid(AsteroidOrbitalElements elements, double julianDate)
        {
            ValidateAsteroid(elements);

            double semiMajorAxis = elements.SemiMajorAxis_AU;
            double eccentricity = elements.Eccentricity;
            double meanAnomaly = NormalizeRadians(
                elements.MeanAnomaly_Deg * DegreesToRadians
                + MeanMotionRadiansPerDay(semiMajorAxis) * (julianDate - elements.Epoch_JD));

            return ComputeFromEllipticalOrbit(
                semiMajorAxis,
                eccentricity,
                elements.Inclination_Deg,
                elements.LongAscendingNode_Deg,
                elements.ArgPerihelion_Deg,
                meanAnomaly,
                julianDate);
        }

        public static SmallBodyEquatorialPosition ComputeComet(CometOrbitalElements elements, double julianDate)
        {
            ValidateComet(elements);

            double semiMajorAxis = elements.PerihelionDistance_AU / (1.0 - elements.Eccentricity);
            double meanAnomaly = NormalizeRadians(
                MeanMotionRadiansPerDay(semiMajorAxis) * (julianDate - elements.PerihelionEpoch_JD));

            return ComputeFromEllipticalOrbit(
                semiMajorAxis,
                elements.Eccentricity,
                elements.Inclination_Deg,
                elements.LongAscendingNode_Deg,
                elements.ArgPerihelion_Deg,
                meanAnomaly,
                julianDate);
        }

        private static SmallBodyEquatorialPosition ComputeFromEllipticalOrbit(
            double semiMajorAxisAu,
            double eccentricity,
            double inclinationDeg,
            double ascendingNodeDeg,
            double argumentOfPerihelionDeg,
            double meanAnomalyRad,
            double julianDate)
        {
            double eccentricAnomaly = SolveKeplerEquation(meanAnomalyRad, eccentricity);
            double radiusAu = semiMajorAxisAu * (1.0 - eccentricity * Math.Cos(eccentricAnomaly));

            double trueAnomaly = 2.0 * Math.Atan2(
                Math.Sqrt(1.0 + eccentricity) * Math.Sin(eccentricAnomaly / 2.0),
                Math.Sqrt(1.0 - eccentricity) * Math.Cos(eccentricAnomaly / 2.0));

            var heliocentricBody = ToHeliocentricEcliptic(
                radiusAu,
                trueAnomaly,
                inclinationDeg,
                ascendingNodeDeg,
                argumentOfPerihelionDeg);

            var heliocentricEarth = ComputeEarthHeliocentricEcliptic(julianDate);

            double geocentricX = heliocentricBody.X - heliocentricEarth.X;
            double geocentricY = heliocentricBody.Y - heliocentricEarth.Y;
            double geocentricZ = heliocentricBody.Z - heliocentricEarth.Z;

            double obliquityRad = GetMeanObliquityRadians(julianDate);

            double equatorialX = geocentricX;
            double equatorialY = geocentricY * Math.Cos(obliquityRad) - geocentricZ * Math.Sin(obliquityRad);
            double equatorialZ = geocentricY * Math.Sin(obliquityRad) + geocentricZ * Math.Cos(obliquityRad);

            double rightAscensionRad = NormalizeRadians(Math.Atan2(equatorialY, equatorialX));
            double declinationRad = Math.Atan2(
                equatorialZ,
                Math.Sqrt(equatorialX * equatorialX + equatorialY * equatorialY));

            return new SmallBodyEquatorialPosition(
                rightAscensionRad * 12.0 / Math.PI,
                declinationRad * RadiansToDegrees);
        }

        private static HeliocentricVector ComputeEarthHeliocentricEcliptic(double julianDate)
        {
            double centuries = (julianDate - J2000) / 36525.0;

            double semiMajorAxis = 1.000001018;
            double eccentricity = 0.01670862 - 0.000042037 * centuries - 0.0000001236 * centuries * centuries;
            double meanLongitudeDeg = 100.466457 + 35999.3728565 * centuries;
            double longitudeOfPerihelionDeg = 102.937348 + 1.7195366 * centuries + 0.00045688 * centuries * centuries;
            double meanAnomalyRad = NormalizeRadians((meanLongitudeDeg - longitudeOfPerihelionDeg) * DegreesToRadians);
            double eccentricAnomaly = SolveKeplerEquation(meanAnomalyRad, eccentricity);
            double radiusAu = semiMajorAxis * (1.0 - eccentricity * Math.Cos(eccentricAnomaly));

            double trueAnomaly = 2.0 * Math.Atan2(
                Math.Sqrt(1.0 + eccentricity) * Math.Sin(eccentricAnomaly / 2.0),
                Math.Sqrt(1.0 - eccentricity) * Math.Cos(eccentricAnomaly / 2.0));

            double eclipticLongitude = NormalizeRadians(trueAnomaly + longitudeOfPerihelionDeg * DegreesToRadians);

            return new HeliocentricVector(
                radiusAu * Math.Cos(eclipticLongitude),
                radiusAu * Math.Sin(eclipticLongitude),
                0.0);
        }

        private static HeliocentricVector ToHeliocentricEcliptic(
            double radiusAu,
            double trueAnomalyRad,
            double inclinationDeg,
            double ascendingNodeDeg,
            double argumentOfPerihelionDeg)
        {
            double inclinationRad = inclinationDeg * DegreesToRadians;
            double ascendingNodeRad = ascendingNodeDeg * DegreesToRadians;
            double argumentOfLatitude = argumentOfPerihelionDeg * DegreesToRadians + trueAnomalyRad;

            double cosNode = Math.Cos(ascendingNodeRad);
            double sinNode = Math.Sin(ascendingNodeRad);
            double cosInclination = Math.Cos(inclinationRad);
            double sinInclination = Math.Sin(inclinationRad);
            double cosArgument = Math.Cos(argumentOfLatitude);
            double sinArgument = Math.Sin(argumentOfLatitude);

            return new HeliocentricVector(
                radiusAu * (cosNode * cosArgument - sinNode * sinArgument * cosInclination),
                radiusAu * (sinNode * cosArgument + cosNode * sinArgument * cosInclination),
                radiusAu * sinArgument * sinInclination);
        }

        private static double MeanMotionRadiansPerDay(double semiMajorAxisAu)
        {
            return GaussianGravitationalConstant / Math.Pow(semiMajorAxisAu, 1.5);
        }

        private static double SolveKeplerEquation(double meanAnomalyRad, double eccentricity)
        {
            double eccentricAnomaly = eccentricity < 0.8 ? meanAnomalyRad : Math.PI;

            for (int i = 0; i < 30; i++)
            {
                double delta = (eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly) - meanAnomalyRad)
                    / (1.0 - eccentricity * Math.Cos(eccentricAnomaly));

                eccentricAnomaly -= delta;

                if (Math.Abs(delta) < 1e-12)
                    break;
            }

            return eccentricAnomaly;
        }

        private static double GetMeanObliquityRadians(double julianDate)
        {
            double centuries = (julianDate - J2000) / 36525.0;
            double arcseconds = 84381.406
                - 46.836769 * centuries
                - 0.0001831 * centuries * centuries
                + 0.00200340 * centuries * centuries * centuries;

            return arcseconds / 3600.0 * DegreesToRadians;
        }

        private static double NormalizeRadians(double angleRad)
        {
            double normalized = angleRad % TwoPi;
            return normalized < 0 ? normalized + TwoPi : normalized;
        }

        private static void ValidateAsteroid(AsteroidOrbitalElements elements)
        {
            ArgumentNullException.ThrowIfNull(elements);

            if (elements.SemiMajorAxis_AU <= 0)
                throw new ArgumentOutOfRangeException(nameof(elements.SemiMajorAxis_AU), "Semi-major axis must be greater than zero.");
            if (elements.Eccentricity < 0 || elements.Eccentricity >= 1)
                throw new ArgumentOutOfRangeException(nameof(elements.Eccentricity), "Asteroid tracking currently supports 0 <= e < 1.");
        }

        private static void ValidateComet(CometOrbitalElements elements)
        {
            ArgumentNullException.ThrowIfNull(elements);

            if (elements.PerihelionDistance_AU <= 0)
                throw new ArgumentOutOfRangeException(nameof(elements.PerihelionDistance_AU), "Perihelion distance must be greater than zero.");
            if (elements.Eccentricity < 0 || elements.Eccentricity >= 1)
                throw new ArgumentOutOfRangeException(nameof(elements.Eccentricity), "Comet tracking currently supports elliptical orbits with 0 <= e < 1.");
        }

        private readonly record struct HeliocentricVector(double X, double Y, double Z);
    }

    internal readonly record struct SmallBodyEquatorialPosition(double RightAscensionHours, double DeclinationDeg);
}
