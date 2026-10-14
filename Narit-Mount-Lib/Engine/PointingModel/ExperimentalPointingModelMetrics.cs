using System;

namespace ForkMount_WPF.Engine.PointingModel
{
    public enum PointingModelBackendMode
    {
        LegacyPxpReload = 0,
        ExperimentalCoreDirect = 1
    }

    public sealed class ExperimentalCalibrationSample
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public double RaJ2000_Rads { get; set; }
        public double DecJ2000_Rads { get; set; }
        public double ThetaEnc_Rads { get; set; }
        public double PhiEnc_Rads { get; set; }
        public double Lst_Rads { get; set; }
        public double JulianDate { get; set; }
        public DateTime CaptureTimeUtc { get; set; }
        public bool IsIncludedInModel { get; set; } = true;
    }

    public sealed class ExperimentalPointingModelMetrics
    {
        private const double ArcsecPerRadian = 206264.80624709636;

        public PointingModelBackendMode BackendMode { get; set; } = PointingModelBackendMode.ExperimentalCoreDirect;
        public bool IsCalibrated { get; set; }
        public int NumCalPoints { get; set; }
        public int NumPointsUsed { get; set; }
        public int NumErrorTermsInUse { get; set; }
        public double RMSErrorRad { get; set; }
        public double RMSErrorAllRad { get; set; }
        public double MaxErrorRad { get; set; }
        public double MaxErrorAllRad { get; set; }
        public string StatusMessage { get; set; } = string.Empty;

        public double RMSErrorArcsec => RMSErrorRad * ArcsecPerRadian;
        public double RMSErrorAllArcsec => RMSErrorAllRad * ArcsecPerRadian;
        public double MaxErrorArcsec => MaxErrorRad * ArcsecPerRadian;
        public double MaxErrorAllArcsec => MaxErrorAllRad * ArcsecPerRadian;

        public string Summary
        {
            get
            {
                if (!IsCalibrated)
                {
                    return $"Waiting for enough points ({NumPointsUsed}/{Math.Max(3, NumCalPoints)})";
                }

                return $"Core RMS {RMSErrorArcsec:F2}\" | Max {MaxErrorArcsec:F2}\" | Used {NumPointsUsed}/{NumCalPoints}";
            }
        }

        public static ExperimentalPointingModelMetrics CreatePending(int totalPoints, int usedPoints, string statusMessage)
        {
            return new ExperimentalPointingModelMetrics
            {
                NumCalPoints = totalPoints,
                NumPointsUsed = usedPoints,
                StatusMessage = statusMessage ?? string.Empty,
                IsCalibrated = false
            };
        }
    }
}
