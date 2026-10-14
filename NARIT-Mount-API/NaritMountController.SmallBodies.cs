using System.Diagnostics;
using NARIT.Mount.API.Internal;
using NARIT.Mount.API.Models;
using ForkMount_WPF.Engine.Utility;

namespace NARIT.Mount.API
{
    public partial class NaritMountController
    {
        private System.Timers.Timer _smallBodyUpdateTimer;
        private Func<double, SmallBodyEquatorialPosition> _smallBodyResolver;
        private TrackingModeType _smallBodyMode = TrackingModeType.None;
        private string _smallBodyName = "";

        public void TrackAsteroid(AsteroidOrbitalElements elements)
        {
            ArgumentNullException.ThrowIfNull(elements);

            StartSmallBodyTracking(
                elements.Name,
                TrackingModeType.Asteroid,
                jd => SmallBodyEphemerisCalculator.ComputeAsteroid(elements, jd));
        }

        public void TrackComet(CometOrbitalElements elements)
        {
            ArgumentNullException.ThrowIfNull(elements);

            StartSmallBodyTracking(
                elements.Name,
                TrackingModeType.Comet,
                jd => SmallBodyEphemerisCalculator.ComputeComet(elements, jd));
        }

        private void StartSmallBodyTracking(
            string objectName,
            TrackingModeType trackingMode,
            Func<double, SmallBodyEquatorialPosition> resolver)
        {
            ThrowIfNotConnected();
            ThrowIfNoObserverSite();

            StopTrackingInternal();

            string resolvedName = string.IsNullOrWhiteSpace(objectName) ? trackingMode.ToString() : objectName.Trim();
            double currentJulianDate = PWLib.Astro.AstroTime.JulianDay(NtpTimeService.Instance.GetCurrentUtcTime());
            SmallBodyEquatorialPosition initialPosition = resolver(currentJulianDate);

            var observer = ToInternalObserverLocation();
            _siderealTracking.SetTarget(initialPosition.RightAscensionHours, initialPosition.DeclinationDeg, observer, isApparent: false);
            _siderealTracking.StartTracking();

            _smallBodyResolver = resolver;
            _smallBodyMode = trackingMode;
            _smallBodyName = resolvedName;

            _smallBodyUpdateTimer?.Stop();
            _smallBodyUpdateTimer?.Dispose();
            _smallBodyUpdateTimer = new System.Timers.Timer(1000);
            _smallBodyUpdateTimer.AutoReset = true;
            _smallBodyUpdateTimer.Elapsed += SmallBodyUpdateTimer_Elapsed;
            _smallBodyUpdateTimer.Start();

            SetTrackingMode(trackingMode, resolvedName);
            Debug.WriteLine($"[NARIT-API] {trackingMode} tracking started: {resolvedName} RA={initialPosition.RightAscensionHours:F6}h Dec={initialPosition.DeclinationDeg:F6} deg");
        }

        private void SmallBodyUpdateTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_smallBodyResolver == null || _observerSite == null || _siderealTracking == null || !_siderealTracking.IsTracking)
                return;

            try
            {
                double currentJulianDate = PWLib.Astro.AstroTime.JulianDay(NtpTimeService.Instance.GetCurrentUtcTime());
                SmallBodyEquatorialPosition updatedPosition = _smallBodyResolver(currentJulianDate);

                _siderealTracking.SetTarget(
                    updatedPosition.RightAscensionHours,
                    updatedPosition.DeclinationDeg,
                    ToInternalObserverLocation(),
                    isApparent: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NARIT-API] Small-body update error for {_smallBodyName}: {ex.Message}");
            }
        }

        private void StopSmallBodyTrackingInternal()
        {
            if (_smallBodyUpdateTimer != null)
            {
                _smallBodyUpdateTimer.Elapsed -= SmallBodyUpdateTimer_Elapsed;
                _smallBodyUpdateTimer.Stop();
                _smallBodyUpdateTimer.Dispose();
                _smallBodyUpdateTimer = null;
            }

            _smallBodyResolver = null;
            _smallBodyMode = TrackingModeType.None;
            _smallBodyName = "";
        }
    }
}
