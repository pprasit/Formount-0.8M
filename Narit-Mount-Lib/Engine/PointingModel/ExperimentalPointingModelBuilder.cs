using System;
using System.Collections.Generic;
using System.Linq;
using ForkMount_WPF.Engine.Utility;
using PWLib.Astro;
using PXPLib;

namespace ForkMount_WPF.Engine.PointingModel
{
    public sealed class ExperimentalPointingModelBuilder
    {
        public PointXP BuildModel(
            IEnumerable<ExperimentalCalibrationSample> samples,
            out ExperimentalPointingModelMetrics metrics,
            out string errorMessage)
        {
            metrics = ExperimentalPointingModelMetrics.CreatePending(0, 0, "No samples");
            errorMessage = string.Empty;

            var sampleList = samples?.ToList() ?? new List<ExperimentalCalibrationSample>();
            int includedCount = sampleList.Count(s => s.IsIncludedInModel);
            metrics = ExperimentalPointingModelMetrics.CreatePending(
                sampleList.Count,
                includedCount,
                includedCount >= 3 ? "Ready to calibrate" : "Need at least 3 included points");

            try
            {
                PointXP pointXp = CreateConfiguredPointXP();
                pointXp.Core.ClearCalPoints();

                foreach (var sample in sampleList)
                {
                    var calPoint = new CalPoint
                    {
                        Name = string.IsNullOrWhiteSpace(sample.Name) ? $"Point {sample.Index}" : sample.Name,
                        RA = sample.RaJ2000_Rads,
                        Dec = sample.DecJ2000_Rads,
                        ThetaEnc = sample.ThetaEnc_Rads,
                        PhiEnc = sample.PhiEnc_Rads,
                        LSTMark = sample.Lst_Rads,
                        JDMark = sample.JulianDate,
                        TimeStr = sample.CaptureTimeUtc.ToString("HH:mm:ss"),
                        DateStr = sample.CaptureTimeUtc.ToString("yyyy-MM-dd"),
                        Used = sample.IsIncludedInModel
                    };

                    pointXp.Core.AddCalPoint(calPoint);
                    pointXp.Core.UseCalPoint(calPoint, sample.IsIncludedInModel);
                }

                if (includedCount < 3)
                {
                    errorMessage = "Need at least 3 included solved points before PointXPCore can calibrate.";
                    return pointXp;
                }

                pointXp.Core.Calibrate();

                metrics = new ExperimentalPointingModelMetrics
                {
                    BackendMode = PointingModelBackendMode.ExperimentalCoreDirect,
                    IsCalibrated = true,
                    NumCalPoints = pointXp.Core.NumCalPoints,
                    NumPointsUsed = pointXp.Core.NumPointsUsed,
                    NumErrorTermsInUse = pointXp.Core.NumErrorTermsInUse,
                    RMSErrorRad = pointXp.Core.RMSErrorRad,
                    RMSErrorAllRad = pointXp.Core.RMSErrorAllRad,
                    MaxErrorRad = pointXp.Core.MaxErrorRad,
                    MaxErrorAllRad = pointXp.Core.MaxErrorAllRad,
                    StatusMessage = "PointXPCore calibrated in memory"
                };

                return pointXp;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                metrics.StatusMessage = $"Experimental calibrate failed: {ex.Message}";
                return null;
            }
        }

        private static PointXP CreateConfiguredPointXP()
        {
            var observerLocation = AppSettings.GetObserverLocation();
            var pointXp = new PointXP
            {
                MountType = (short)0,
                Latitude = observerLocation.Latitude_Deg,
                Longitude = observerLocation.Longitude_Deg,
                ThetaCountsPerRev = 2.0 * Math.PI,
                PhiCountsPerRev = 2.0 * Math.PI
            };

            pointXp.EnableTermsForAltAz();
            pointXp.LoadDateTimeUTCFromComputer();

            return pointXp;
        }
    }
}
