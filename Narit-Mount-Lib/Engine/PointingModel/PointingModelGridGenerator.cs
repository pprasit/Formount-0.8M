using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace ForkMount_WPF.Engine.PointingModel
{
    /// <summary>
    /// Grid generator for pointing model calibration
    /// Generates Alt/Az grid points for sky coverage
    /// Adapted from NaritMountLib.PointingModel.BuildPointingModel
    /// </summary>
    public class PointingModelGridGenerator
    {
        // Grid parameters
        private double _azmMin = 0.0;
        private double _azmMax = 360.0;
        private double _azmSpacing = 90.0;
        private double _altMin = 10.0;
        private double _altMax = 85.0;
        private double _altSpacing = 45.0;
        
        // Calibration settings
        private double _exposureTimeSec = 3.0;
        private double _arcsecPerPixUnbinned = 0.60;
        private int _binning = 2;
        
        /// <summary>
        /// Minimum azimuth (degrees)
        /// </summary>
        public double AzimuthMin
        {
            get => _azmMin;
            set => _azmMin = value;
        }
        
        /// <summary>
        /// Maximum azimuth (degrees)
        /// </summary>
        public double AzimuthMax
        {
            get => _azmMax;
            set => _azmMax = value;
        }
        
        /// <summary>
        /// Azimuth spacing (degrees)
        /// </summary>
        public double AzimuthSpacing
        {
            get => _azmSpacing;
            set => _azmSpacing = value;
        }
        
        /// <summary>
        /// Minimum altitude (degrees)
        /// </summary>
        public double AltitudeMin
        {
            get => _altMin;
            set => _altMin = value;
        }
        
        /// <summary>
        /// Maximum altitude (degrees)
        /// </summary>
        public double AltitudeMax
        {
            get => _altMax;
            set => _altMax = value;
        }
        
        /// <summary>
        /// Altitude spacing (degrees)
        /// </summary>
        public double AltitudeSpacing
        {
            get => _altSpacing;
            set => _altSpacing = value;
        }
        
        /// <summary>
        /// Exposure time (seconds)
        /// </summary>
        public double ExposureTime
        {
            get => _exposureTimeSec;
            set => _exposureTimeSec = value;
        }
        
        /// <summary>
        /// Plate scale at binning 1x1 (arcsec/pixel)
        /// </summary>
        public double PlateScaleUnbinned
        {
            get => _arcsecPerPixUnbinned;
            set => _arcsecPerPixUnbinned = value;
        }
        
        /// <summary>
        /// Camera binning
        /// </summary>
        public int Binning
        {
            get => _binning;
            set => _binning = value;
        }
        
        /// <summary>
        /// Generate Alt/Az grid points
        /// Logic from Narit's GenerateAltAzPoints()
        /// </summary>
        public List<AltAzPoint> GenerateGrid()
        {
            List<AltAzPoint> points = new List<AltAzPoint>();
            
            double azm = _azmMin;
            int index = 1;
            
            while (azm < _azmMax)
            {
                double alt = _altMin;
                
                while (alt < _altMax)
                {
                    // Skip points below 10° altitude (horizon limit)
                    if (alt >= 10.0)
                    {
                        points.Add(new AltAzPoint
                        {
                            Index = index++,
                            Azimuth = azm,
                            Altitude = alt
                        });
                    }
                    
                    alt += _altSpacing;
                }
                
                azm += _azmSpacing;
            }
            
            return points;
        }
        
        /// <summary>
        /// Generate grid with custom parameters
        /// </summary>
        public List<AltAzPoint> GenerateGrid(
            double azmSpacing, 
            double azmMin, 
            double azmMax, 
            double altSpacing, 
            double altMin, 
            double altMax)
        {
            _azmSpacing = azmSpacing;
            _azmMin = azmMin;
            _azmMax = azmMax;
            _altSpacing = altSpacing;
            _altMin = altMin;
            _altMax = altMax;
            
            return GenerateGrid();
        }
        
        /// <summary>
        /// Generate default grid (4 azimuth × 2 altitude = 8 points)
        /// Good for quick testing
        /// </summary>
        public static List<AltAzPoint> GenerateDefaultGrid()
        {
            var generator = new PointingModelGridGenerator
            {
                AzimuthMin = 0.0,
                AzimuthMax = 360.0,
                AzimuthSpacing = 90.0,  // 0°, 90°, 180°, 270°
                AltitudeMin = 30.0,
                AltitudeMax = 85.0,
                AltitudeSpacing = 45.0  // 30°, 75°
            };
            
            return generator.GenerateGrid();
        }
        
        /// <summary>
        /// Generate dense grid (8 azimuth × 4 altitude = 32 points)
        /// For high-accuracy model
        /// </summary>
        public static List<AltAzPoint> GenerateDenseGrid()
        {
            var generator = new PointingModelGridGenerator
            {
                AzimuthMin = 0.0,
                AzimuthMax = 360.0,
                AzimuthSpacing = 45.0,  // 8 points in azimuth
                AltitudeMin = 20.0,
                AltitudeMax = 85.0,
                AltitudeSpacing = 20.0  // 4 points in altitude
            };
            
            return generator.GenerateGrid();
        }
        
        /// <summary>
        /// Calculate total number of points for current settings
        /// </summary>
        public int CalculatePointCount()
        {
            int azmCount = (int)Math.Ceiling((_azmMax - _azmMin) / _azmSpacing);
            int altCount = (int)Math.Ceiling((_altMax - _altMin) / _altSpacing);
            return azmCount * altCount;
        }
        
        /// <summary>
        /// Estimate total calibration time (minutes)
        /// </summary>
        public double EstimateDuration()
        {
            int pointCount = CalculatePointCount();
            
            // Time per point:
            // - Slew: 15 sec average
            // - Settle: 3 sec
            // - Exposure: configurable
            // - Download: 2 sec
            // - Plate solve: 5 sec average
            // Total: ~25 sec + exposure
            
            double timePerPoint = 25.0 + _exposureTimeSec;
            return (pointCount * timePerPoint) / 60.0;  // Convert to minutes
        }
    }
    
    /// <summary>
    /// Alt/Az point for grid generation
    /// Simple data structure (no dependencies on PWLib)
    /// </summary>
    public class AltAzPoint
    {
        public int Index { get; set; }
        public double Azimuth { get; set; }
        public double Altitude { get; set; }
        
        public override string ToString()
        {
            return $"Point {Index}: Az={Azimuth:F1}°, Alt={Altitude:F1}°";
        }
    }
}
