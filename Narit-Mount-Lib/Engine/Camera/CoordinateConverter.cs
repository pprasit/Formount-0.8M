using System;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// Coordinate conversion utilities for astronomy
    /// For RA/Dec ? Az/Alt conversion, use PWLib.Astro.RaDec and PWLib.Astro.AltAz instead.
    /// This class provides image projection and angular separation utilities.
    /// </summary>
    public static class CoordinateConverter
    {
        private const double DEG_TO_RAD = Math.PI / 180.0;
        private const double RAD_TO_DEG = 180.0 / Math.PI;

        /// <summary>
        /// Calculate angular separation between two points (degrees)
        /// </summary>
        public static double AngularSeparation(double ra1, double dec1, double ra2, double dec2)
        {
            double ra1Rad = ra1 * DEG_TO_RAD;
            double dec1Rad = dec1 * DEG_TO_RAD;
            double ra2Rad = ra2 * DEG_TO_RAD;
            double dec2Rad = dec2 * DEG_TO_RAD;

            double cosSep = Math.Sin(dec1Rad) * Math.Sin(dec2Rad) +
                           Math.Cos(dec1Rad) * Math.Cos(dec2Rad) * Math.Cos(ra1Rad - ra2Rad);

            cosSep = Math.Max(-1.0, Math.Min(1.0, cosSep));

            return Math.Acos(cosSep) * RAD_TO_DEG;
        }

        /// <summary>
        /// Convert RA/Dec to image pixel coordinates
        /// Uses Gnomonic (tangent plane) projection
        /// </summary>
        /// <param name="starRA">Star RA (degrees)</param>
        /// <param name="starDec">Star Dec (degrees)</param>
        /// <param name="centerRA">Image center RA (degrees)</param>
        /// <param name="centerDec">Image center Dec (degrees)</param>
        /// <param name="plateScale">Plate scale (arcsec/pixel)</param>
        /// <param name="imageWidth">Image width (pixels)</param>
        /// <param name="imageHeight">Image height (pixels)</param>
        /// <param name="x">Output X pixel coordinate</param>
        /// <param name="y">Output Y pixel coordinate</param>
        /// <returns>True if star is within image bounds</returns>
        public static bool RaDecToImageXY(
            double starRA, double starDec,
            double centerRA, double centerDec,
            double plateScale,
            int imageWidth, int imageHeight,
            out int x, out int y)
        {
            // Convert to radians
            double starRArad = starRA * DEG_TO_RAD;
            double starDecRad = starDec * DEG_TO_RAD;
            double centerRArad = centerRA * DEG_TO_RAD;
            double centerDecRad = centerDec * DEG_TO_RAD;

            // Handle RA wrap-around
            double deltaRArad = starRArad - centerRArad;
            if (deltaRArad > Math.PI) deltaRArad -= 2 * Math.PI;
            if (deltaRArad < -Math.PI) deltaRArad += 2 * Math.PI;

            // Gnomonic projection (tangent plane)
            // Standard formulae from astrometry
            double cosDec = Math.Cos(starDecRad);
            double sinDec = Math.Sin(starDecRad);
            double cosCenterDec = Math.Cos(centerDecRad);
            double sinCenterDec = Math.Sin(centerDecRad);
            double cosDeltaRA = Math.Cos(deltaRArad);

            // Calculate denominator for projection
            double denom = sinCenterDec * sinDec + cosCenterDec * cosDec * cosDeltaRA;

            if (denom <= 0)
            {
                // Star is behind the projection plane (> 90 degrees away)
                x = -1;
                y = -1;
                return false;
            }

            // Xi and Eta (standard coordinates in radians)
            double xi = (cosDec * Math.Sin(deltaRArad)) / denom;
            double eta = (cosCenterDec * sinDec - sinCenterDec * cosDec * cosDeltaRA) / denom;

            // Convert to arcseconds
            double xi_arcsec = xi * RAD_TO_DEG * 3600.0;
            double eta_arcsec = eta * RAD_TO_DEG * 3600.0;

            // Convert to pixels
            double pixelX = xi_arcsec / plateScale;
            double pixelY = -eta_arcsec / plateScale; // Y axis inverted

            // Convert to image coordinates (center of image)
            x = (int)(imageWidth / 2.0 + pixelX);
            y = (int)(imageHeight / 2.0 + pixelY);

            // Check if within image bounds
            bool inBounds = (x >= 0 && x < imageWidth && y >= 0 && y < imageHeight);
            
            return inBounds;
        }
    }
}
