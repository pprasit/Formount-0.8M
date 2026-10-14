using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Catalog;
using Microsoft.Win32;
using NARIT.Mount.API;
using NARIT.Mount.API.Events;
using NARIT.Mount.API.Interfaces;
using NARIT.Mount.API.Models;
using PWLib.Astro;

namespace NARIT.Mount.API.Tester
{
    public partial class MainWindow : Window
    {
        private NaritMountController _mount;
        private DispatcherTimer _refreshTimer;
        private bool _isConnected;
        private bool _isConnecting;
        private readonly ObservableCollection<BrightStarRow> _brightStars = new();
        private readonly List<BrightStarRow> _allBrightStars = new();
        private DateTime _lastBrightStarRefreshUtc = DateTime.MinValue;
        private ListSortDirection _trackColumnSortDirection = ListSortDirection.Descending;
        private static readonly SolidColorBrush TrackableBrush = CreateBrush(16, 185, 129);
        private static readonly SolidColorBrush NotTrackableBrush = CreateBrush(107, 114, 128);

        public MainWindow()
        {
            InitializeComponent();
            _mount = new NaritMountController();
            Loaded += MainWindow_Loaded;
            InitializeRefreshTimer();
            chkAutoRefresh.IsChecked = true;
            RestorePersistedConfigurationToUi();
            PopulateCategory();
            UpdateOffsetDisplay();
            InitializeBrightStarCatalog();
            dgBrightStars.ItemsSource = _brightStars;
            RefreshBrightStarCatalog(force: true);
            AddLog("NARIT Mount API Test Client ready.");
            AddLog("Persisted observer site and pointing model path restored.");
            AddLog("Auto-connect will start when the window is ready.");
        }

        #region Refresh Timer

        private void InitializeRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (SyncConnectionStateFromController() && _mount != null)
                    RefreshStatus();

                RefreshBrightStarCatalog();
            }
            catch { /* ignore transient read errors */ }
        }

        private void ChkAutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (_refreshTimer == null)
                return;

            if (chkAutoRefresh.IsChecked == true)
                _refreshTimer.Start();
            else
                _refreshTimer.Stop();
        }

        #endregion

        #region Tab 1 � Connection

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            await ConnectMountAsync(isAutoStartup: true);
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            await ConnectMountAsync(isAutoStartup: false);
        }

        private async Task ConnectMountAsync(bool isAutoStartup)
        {
            if (_isConnecting || SyncConnectionStateFromController())
                return;

            try
            {
                _isConnecting = true;
                btnConnect.IsEnabled = false;
                AddLog(isAutoStartup
                    ? "Auto-connecting to mount with persisted settings..."
                    : "Connecting to mount...");

                ReplaceMountController(new NaritMountController());
                RestorePersistedConfigurationToUi();

                bool ok = await _mount.ConnectAsync();
                if (ok)
                {
                    SyncConnectionStateFromController();
                    chkAutoRefresh.IsChecked = true;

                    if (isAutoStartup)
                    {
                        _mount.ResetOffset();
                        AddLog("Startup reset applied: telescope offset cleared to 0 arcsec on both axes.");
                    }

                    RefreshStatus();
                    UpdateOffsetDisplay();
                    LogMountDiagnostics(isAutoStartup ? "Startup" : "Connect");
                    AddLog(isAutoStartup ? "Auto-connect completed." : "? Connected successfully!");
                }
                else
                {
                    _isConnected = false;
                    AddLog(isAutoStartup ? "? Auto-connect failed." : "? Connection failed.");
                    SetConnectionUI(false);
                }
            }
            catch (Exception ex)
            {
                _isConnected = false;
                SetConnectionUI(false);
                AddLog(isAutoStartup ? $"? Auto-connect error: {ex.Message}" : $"? Connect error: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
                btnConnect.IsEnabled = !_isConnected;
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _mount?.Disconnect();
                SyncConnectionStateFromController();
                _refreshTimer.Stop();
                chkAutoRefresh.IsChecked = false;
                ClearStatusFields();
                AddLog("Disconnected.");
            }
            catch (Exception ex)
            {
                AddLog($"? Disconnect error: {ex.Message}");
            }
        }

        private void SetConnectionUI(bool connected)
        {
            dotConnection.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));
            txtConnectionState.Text = connected ? "Connected" : "Disconnected";
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
        }

        #endregion

        #region Tab 1 � Commands

        private void RestorePersistedConfigurationToUi()
        {
            try
            {
                if (_mount == null)
                    return;

                var site = _mount.GetObserverSite();
                if (site != null)
                {
                    txtLat.Text = site.Latitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    txtLon.Text = site.Longitude.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    txtElev.Text = site.Elevation.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                    txtUtcOffset.Text = site.TimezoneUtcOffset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                }

                string pointingModelPath = _mount.GetPointingModelPath();
                if (!string.IsNullOrWhiteSpace(pointingModelPath))
                    txtPointingModelPath.Text = pointingModelPath;

                RefreshBrightStarCatalog(force: true);
            }
            catch (Exception ex)
            {
                AddLog($"? Restore saved configuration: {ex.Message}");
            }
        }

        private void BtnSetSite_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var site = new ObserverSite(
                    double.Parse(txtLat.Text),
                    double.Parse(txtLon.Text),
                    double.Parse(txtElev.Text),
                    double.Parse(txtUtcOffset.Text));
                _mount.SetObserverSite(site);
                RefreshBrightStarCatalog(force: true);
                AddLog($"Observer site set: Lat={site.Latitude:F4}� Lon={site.Longitude:F4}� Elev={site.Elevation:F0}m");
            }
            catch (Exception ex) { AddLog($"? SetObserverSite: {ex.Message}"); }
        }

        private void BtnBrowsePointingModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Pointing Model File",
                Filter = "Pointing Model Files (*.pxp;*.pmd)|*.pxp;*.pmd|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                txtPointingModelPath.Text = dialog.FileName;
            }
        }

        private void BtnSetPointingModelPath_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                string filePath = txtPointingModelPath.Text.Trim();
                _mount.SetPointingModelPath(filePath);
                RefreshStatus();

                var pos = _mount.GetPosition();
                AddLog($"SetPointingModelPath(\"{filePath}\") loaded successfully.");
                AddLog($"Pointing model active snapshot -> Az_Raw={pos.Azimuth_Raw_Deg:F4} deg Alt_Raw={pos.Altitude_Raw_Deg:F4} deg " +
                       $"Az_Corr={pos.Azimuth_Corrected_Deg:F4} deg Alt_Corr={pos.Altitude_Corrected_Deg:F4} deg");
            }
            catch (Exception ex)
            {
                AddLog($"? SetPointingModelPath: {ex.Message}");
            }
        }

        private void BtnEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try { _mount.Enable(); RefreshStatus(); AddLog("Enable() sent."); }
            catch (Exception ex) { AddLog($"? Enable: {ex.Message}"); }
        }

        private void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try { _mount.Disable(); RefreshStatus(); AddLog("Disable() sent."); }
            catch (Exception ex) { AddLog($"? Disable: {ex.Message}"); }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try { _mount.Stop(); RefreshStatus(); AddLog("Stop() sent."); }
            catch (Exception ex) { AddLog($"? Stop: {ex.Message}"); }
        }

        private void BtnGoto_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                double az = double.Parse(txtGotoAz.Text);
                double alt = double.Parse(txtGotoAlt.Text);
                double vel = double.Parse(txtGotoVel.Text);
                _mount.GotoPosition(az, alt, vel);
                RefreshStatus();
                AddLog($"GotoPosition(Az={az:F2}�, Alt={alt:F2}�, Vel={vel:F1}�/s)");
            }
            catch (Exception ex) { AddLog($"? GotoPosition: {ex.Message}"); }
        }

        private void BtnJog_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                double azVel = double.Parse(txtJogAz.Text);
                double altVel = double.Parse(txtJogAlt.Text);
                _mount.Jog(azVel, altVel);
                RefreshStatus();
                AddLog($"Jog(Az={azVel:F3} deg/s, Alt={altVel:F3} deg/s)");
            }
            catch (Exception ex) { AddLog($"? Jog: {ex.Message}"); }
        }

        private void CmbOffsetType_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateOffsetDisplay();
        }

        private void BtnOffset_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            if (sender is not Button btn || btn.Tag is not string directionTag) return;

            try
            {
                double stepArcsec = double.Parse(txtOffsetStep.Text);
                var offsetType = GetSelectedOffsetType();
                var direction = Enum.Parse<OffsetDirection>(directionTag, false);

                _mount.ApplyOffset(offsetType, stepArcsec, direction);
                RefreshStatus();
                AddLog($"ApplyOffset(Type={offsetType}, Step={stepArcsec:F2} arcsec, Direction={direction})");
            }
            catch (Exception ex)
            {
                AddLog($"? ApplyOffset: {ex.Message}");
            }
        }

        private void BtnResetOffset_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                _mount.ResetOffset();
                RefreshStatus();
                AddLog("ResetOffset()");
            }
            catch (Exception ex)
            {
                AddLog($"? ResetOffset: {ex.Message}");
            }
        }

        private void BtnTrackObject_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                if (dgBrightStars.SelectedItem is not BrightStarRow selectedStar)
                {
                    AddLog("? Select a bright star from the table first.");
                    return;
                }

                string name = selectedStar.Name;

                AddLog(
                    $"Selected star -> {name}, RA={selectedStar.RightAscensionHours:F6} h, " +
                    $"Dec={selectedStar.DeclinationDegrees:F6} deg, " +
                    $"Table Az={selectedStar.AzimuthDegrees:F4} deg, Alt={selectedStar.AltitudeDegrees:F4} deg");

                _mount.TrackObject(selectedStar.RightAscensionHours, selectedStar.DeclinationDegrees, name);
                RefreshStatus();
                AddLog(
                    $"TrackObject(J2000 RA={selectedStar.RightAscensionHours:F6} h, " +
                    $"Dec={selectedStar.DeclinationDegrees:F6} deg, \"{name}\")");
                LogMountDiagnostics("TrackObject");
            }
            catch (Exception ex) { AddLog($"? TrackObject: {ex.Message}"); }
        }

        private void BtnTrackSidereal_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                _mount.TrackSidereal();
                AddLog("TrackSidereal() - tracking current position.");
            }
            catch (Exception ex) { AddLog($"? TrackSidereal: {ex.Message}"); }
        }
        private void BtnStopTracking_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try { _mount.StopTracking(); RefreshStatus(); AddLog("StopTracking()"); }
            catch (Exception ex) { AddLog($"? StopTracking: {ex.Message}"); }
        }

        private void BtnTrackAsteroid_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var elements = new AsteroidOrbitalElements
                {
                    Name = txtAsteroidName.Text.Trim(),
                    Epoch_JD = double.Parse(txtAsteroidEpochJd.Text, System.Globalization.CultureInfo.InvariantCulture),
                    SemiMajorAxis_AU = double.Parse(txtAsteroidA.Text, System.Globalization.CultureInfo.InvariantCulture),
                    Eccentricity = double.Parse(txtAsteroidE.Text, System.Globalization.CultureInfo.InvariantCulture),
                    Inclination_Deg = double.Parse(txtAsteroidI.Text, System.Globalization.CultureInfo.InvariantCulture),
                    LongAscendingNode_Deg = double.Parse(txtAsteroidNode.Text, System.Globalization.CultureInfo.InvariantCulture),
                    ArgPerihelion_Deg = double.Parse(txtAsteroidArgPeri.Text, System.Globalization.CultureInfo.InvariantCulture),
                    MeanAnomaly_Deg = double.Parse(txtAsteroidM.Text, System.Globalization.CultureInfo.InvariantCulture)
                };

                _mount.TrackAsteroid(elements);
                RefreshStatus();
                AddLog($"TrackAsteroid(Name={elements.Name}, EpochJD={elements.Epoch_JD:F5})");
            }
            catch (Exception ex)
            {
                AddLog($"? TrackAsteroid: {ex.Message}");
            }
        }

        private void BtnTrackComet_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var elements = new CometOrbitalElements
                {
                    Name = txtCometName.Text.Trim(),
                    PerihelionEpoch_JD = double.Parse(txtCometPerihelionJd.Text, System.Globalization.CultureInfo.InvariantCulture),
                    PerihelionDistance_AU = double.Parse(txtCometQ.Text, System.Globalization.CultureInfo.InvariantCulture),
                    Eccentricity = double.Parse(txtCometE.Text, System.Globalization.CultureInfo.InvariantCulture),
                    Inclination_Deg = double.Parse(txtCometI.Text, System.Globalization.CultureInfo.InvariantCulture),
                    LongAscendingNode_Deg = double.Parse(txtCometNode.Text, System.Globalization.CultureInfo.InvariantCulture),
                    ArgPerihelion_Deg = double.Parse(txtCometArgPeri.Text, System.Globalization.CultureInfo.InvariantCulture)
                };

                _mount.TrackComet(elements);
                RefreshStatus();
                AddLog($"TrackComet(Name={elements.Name}, PerihelionJD={elements.PerihelionEpoch_JD:F5})");
            }
            catch (Exception ex)
            {
                AddLog($"? TrackComet: {ex.Message}");
            }
        }
        #endregion

        #region Tab 1 � Status Polling

        private void BtnGetStatus_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try { RefreshStatus(); AddLog("GetStatus() called."); }
            catch (Exception ex) { AddLog($"? GetStatus: {ex.Message}"); }
        }

        private void BtnGetPosition_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var pos = _mount.GetPosition();
                AddLog($"GetPosition() ? Az_Raw={pos.Azimuth_Raw_Deg:F4}� Alt_Raw={pos.Altitude_Raw_Deg:F4}� " +
                       $"Az_Corr={pos.Azimuth_Corrected_Deg:F4}� Alt_Corr={pos.Altitude_Corrected_Deg:F4}�");
            }
            catch (Exception ex) { AddLog($"? GetPosition: {ex.Message}"); }
        }

        private void BtnGetTrackingInfo_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var ti = _mount.GetTrackingInfo();
                AddLog($"GetTrackingInfo() ? Mode={ti.Mode} Object=\"{ti.ObjectName}\" OnTarget={ti.IsOnTarget} " +
                       $"RMS: Az={ti.AzimuthRms_Arcsec:F2}\" Alt={ti.AltitudeRms_Arcsec:F2}\"");
            }
            catch (Exception ex) { AddLog($"? GetTrackingInfo: {ex.Message}"); }
        }

        private void RefreshStatus()
        {
            if (_mount == null || !_isConnected) return;

            var s = _mount.GetStatus();
            var pos = s.Position;

            // General
            valState.Text = s.State.ToString();
            valEnabled.Text = s.IsMountEnabled ? "? Yes" : "? No";
            valNtp.Text = s.IsTimeSynchronized ? "? Yes" : "? No";
            valNtpOffset.Text = $"{s.NtpOffsetMs:F1} ms";
            valUtc.Text = s.SystemUtcTime.ToString("HH:mm:ss.f");
            valJd.Text = s.JulianDay > 0 ? $"{s.JulianDay:F6}" : "--";

            // Position
            valAzRaw.Text = $"{pos.Azimuth_Raw_Deg:F4}�";
            valAltRaw.Text = $"{pos.Altitude_Raw_Deg:F4}�";
            valAzCorr.Text = $"{pos.Azimuth_Corrected_Deg:F4}�";
            valAltCorr.Text = $"{pos.Altitude_Corrected_Deg:F4}�";

            // Tracking
            valTrackMode.Text = s.CurrentTrackingMode.ToString();
            valTrackObj.Text = string.IsNullOrEmpty(s.CurrentTrackingObjectName) ? "(none)" : s.CurrentTrackingObjectName;
            valActualRaJ2000.Text = s.HasActualRaDecJ2000
                ? FormatHoursAsSexagesimal(s.ActualRaJ2000_Hours)
                : "--";
            valActualDecJ2000.Text = s.HasActualRaDecJ2000
                ? FormatDegreesAsSexagesimal(s.ActualDecJ2000_Deg)
                : "--";
            valCmdRaJ2000.Text = s.HasCommandRaDec
                ? FormatHoursAsSexagesimal(s.CommandRa_Hours)
                : "--";
            valCmdDecJ2000.Text = s.HasCommandRaDec
                ? FormatDegreesAsSexagesimal(s.CommandDec_Deg)
                : "--";

            // Axes
            valAzAxisStatus.Text = s.AzimuthAxis?.Axis_Status ?? "--";
            valAltAxisStatus.Text = s.AltitudeAxis?.Axis_Status ?? "--";
            valAzCmdVel.Text = $"{s.AzimuthAxis?.CommandVelocity_DegPerSec ?? 0:F4}�/s";
            valAltCmdVel.Text = $"{s.AltitudeAxis?.CommandVelocity_DegPerSec ?? 0:F4}�/s";
            valAzRms.Text = $"{s.AzimuthAxis?.TrackingRmsError_Arcsec ?? 0:F2}\"";
            valAltRms.Text = $"{s.AltitudeAxis?.TrackingRmsError_Arcsec ?? 0:F2}\"";
            UpdateOffsetDisplay(s);

            // Tracking info
            if (s.IsTracking || s.CurrentTrackingMode != TrackingModeType.None)
            {
                try
                {
                    var ti = _mount.GetTrackingInfo();
                    valOnTarget.Text = ti.IsOnTarget ? "? Yes" : "? No";
                    valDuration.Text = ti.Duration.ToString(@"hh\:mm\:ss");
                    valLst.Text = $"{ti.LocalSiderealTime_Hours:F4} h";
                }
                catch
                {
                    valOnTarget.Text = "--";
                    valDuration.Text = "--";
                    valLst.Text = "--";
                }
            }
            else
            {
                valOnTarget.Text = "--";
                valDuration.Text = "--";
                valLst.Text = "--";
            }
        }

        private void DgBrightStars_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedStarInfo();
        }

        private void DgBrightStars_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (e.Column.SortMemberPath != "IsTrackable")
                return;

            e.Handled = true;

            _trackColumnSortDirection = e.Column.SortDirection == ListSortDirection.Descending
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;

            foreach (var column in dgBrightStars.Columns)
            {
                column.SortDirection = null;
            }

            e.Column.SortDirection = _trackColumnSortDirection;
            RefreshBrightStarCatalog(force: true);
        }

        private void ClearStatusFields()
        {
            valState.Text = valEnabled.Text = valNtp.Text = valNtpOffset.Text = valUtc.Text = valJd.Text = "--";
            valAzRaw.Text = valAltRaw.Text = valAzCorr.Text = valAltCorr.Text = "--";
            valTrackMode.Text = valTrackObj.Text = valCmdRaJ2000.Text = valCmdDecJ2000.Text = "--";
            valActualRaJ2000.Text = valActualDecJ2000.Text = "--";
            valOnTarget.Text = valDuration.Text = valLst.Text = "--";
            valAzAxisStatus.Text = valAltAxisStatus.Text = "--";
            valAzCmdVel.Text = valAltCmdVel.Text = valAzRms.Text = valAltRms.Text = "--";
            UpdateOffsetDisplay();
        }

        #endregion

        #region Tab 1 � Event Handlers (Subscribe)

        private void OnPositionChanged(object sender, PositionChangedEventArgs e)
        {
            // High frequency event � don't log every call
        }

        private void OnTrackingStateChanged(object sender, TrackingStateChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                AddLog($"[EVENT] TrackingStateChanged ? IsTracking={e.IsTracking} Mode={e.Mode} Object=\"{e.ObjectName}\"");
            });
        }

        private void OnErrorOccurred(object sender, MountErrorEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                AddLog($"[EVENT] ? ErrorOccurred ? {e.Message} (Recoverable={e.IsRecoverable})");
            });
        }

        #endregion

        #region Tab 1 � Log

        private void AddLog(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.f");
            txtLog.AppendText($"[{ts}] {msg}\n");
            txtLog.ScrollToEnd();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e) => txtLog.Clear();

        #endregion

        #region Tab 2 � Code Generator

        private readonly Dictionary<string, List<MethodInfo>> _methods = new()
        {
            ["Connection"] = new()
            {
                new("ConnectAsync", "Connect to mount hardware", Array.Empty<ParamDef>()),
                new("Disconnect", "Disconnect from mount", Array.Empty<ParamDef>()),
            },
            ["Configuration"] = new()
            {
                new("SetObserverSite", "Set observer location", new[] {
                    new ParamDef("Latitude", "18.5736"),
                    new ParamDef("Longitude", "98.4820"),
                    new ParamDef("Elevation", "2457"),
                    new ParamDef("UTC Offset", "7"),
                }),
                new("SetPointingModelPath", "Load pointing model file", new[] {
                    new ParamDef("File Path", @"C:\Models\pointing.pxp"),
                }),
                new("SetNtpConfig", "Configure NTP sync", new[] {
                    new ParamDef("Server", "pool.ntp.org"),
                    new ParamDef("Interval (s)", "3600"),
                }),
            },
            ["Drive Control"] = new()
            {
                new("Enable", "Enable both axis drives", Array.Empty<ParamDef>()),
                new("Disable", "Disable both axis drives", Array.Empty<ParamDef>()),
                new("Stop", "Emergency stop both axes", Array.Empty<ParamDef>()),
            },
            ["Motion"] = new()
            {
                new("GotoPosition", "Move to Az/Alt position", new[] {
                    new ParamDef("Az (�)", "180.0"),
                    new ParamDef("Alt (�)", "45.0"),
                    new ParamDef("Vel (�/s)", "5.0"),
                }),
                new("MoveVelocity", "Continuous velocity move", new[] {
                    new ParamDef("Az Vel (�/s)", "0.5"),
                    new ParamDef("Alt Vel (�/s)", "0.0"),
                }),
                new("Jog", "Jog both axes", new[] {
                    new ParamDef("Az Vel (deg/s)", "1.0"),
                    new ParamDef("Alt Vel (deg/s)", "0.0"),
                }),
                new("ApplyOffset", "Apply telescope offset by type, step, and direction", new[] {
                    new ParamDef("Offset Type", "OffsetType.RaDec"),
                    new ParamDef("Step (arcsec)", "10"),
                    new ParamDef("Direction", "OffsetDirection.NE"),
                }),
                new("ResetOffset", "Reset accumulated telescope offset", Array.Empty<ParamDef>()),
                new("AzimuthAxis.GotoPosition", "Single-axis goto (Az)", new[] {
                    new ParamDef("Position (�)", "180.0"),
                    new ParamDef("Vel (�/s)", "5.0"),
                }),
                new("AltitudeAxis.GotoPosition", "Single-axis goto (Alt)", new[] {
                    new ParamDef("Position (�)", "45.0"),
                    new ParamDef("Vel (�/s)", "3.0"),
                }),
            },
            ["Tracking"] = new()
            {
                new("TrackObject", "Track celestial object by RA/Dec (J2000)", new[] {
                    new ParamDef("RA (hours)", "5.9195"),
                    new ParamDef("Dec (�)", "7.4072"),
                    new ParamDef("Name", "Betelgeuse"),
                }),
                new("TrackSidereal", "Track current position at sidereal rate", Array.Empty<ParamDef>()),
                new("TrackSatellite", "Track satellite via TLE", new[] {
                    new ParamDef("TLE Line 1", "1 25544U ..."),
                    new ParamDef("TLE Line 2", "2 25544 ..."),
                    new ParamDef("Name", "ISS"),
                }),
                new("StopTracking", "Stop all tracking", Array.Empty<ParamDef>()),
            },
            ["Status (Polling)"] = new()
            {
                new("GetStatus", "Get full mount status snapshot", Array.Empty<ParamDef>()),
                new("GetPosition", "Get current position snapshot", Array.Empty<ParamDef>()),
                new("GetTrackingInfo", "Get tracking details", Array.Empty<ParamDef>()),
                new("AzimuthAxis.GetState", "Get azimuth axis state", Array.Empty<ParamDef>()),
                new("AltitudeAxis.GetState", "Get altitude axis state", Array.Empty<ParamDef>()),
            },
            ["Events (Subscribe)"] = new()
            {
                new("PositionChanged", "Subscribe to position updates (~66Hz)", Array.Empty<ParamDef>()),
                new("TrackingStateChanged", "Subscribe to tracking state changes", Array.Empty<ParamDef>()),
                new("ErrorOccurred", "Subscribe to mount errors", Array.Empty<ParamDef>()),
            },
            ["Full Example"] = new()
            {
                new("Complete Workflow", "Full connect ? track ? disconnect example", Array.Empty<ParamDef>()),
                new("Polling Loop", "Timer-based status polling example", Array.Empty<ParamDef>()),
                new("Event Subscription", "Event-based data subscription example", Array.Empty<ParamDef>()),
            },
        };

        private void PopulateCategory()
        {
            CmbCategory_Changed(null, null);
        }

        private void CmbCategory_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (lstMethods == null) return;
            lstMethods.Items.Clear();

            string cat = (cmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Connection";
            if (_methods.TryGetValue(cat, out var list))
            {
                foreach (var m in list)
                    lstMethods.Items.Add(m.Name);
            }

            if (lstMethods.Items.Count > 0)
                lstMethods.SelectedIndex = 0;
        }

        private void LstMethods_Changed(object sender, SelectionChangedEventArgs e)
        {
            HideAllParams();
            if (lstMethods.SelectedItem == null) return;

            string cat = (cmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            string method = lstMethods.SelectedItem.ToString();

            if (!_methods.TryGetValue(cat, out var list)) return;
            var info = list.Find(m => m.Name == method);
            if (info == null) return;

            ShowParams(info.Params);
            // Auto-generate on selection
            txtCode.Text = GenerateCode(cat, info);
        }

        private void ShowParams(ParamDef[] parms)
        {
            var labels = new[] { lblP1, lblP2, lblP3, lblP4, lblP5 };
            var boxes = new[] { txtP1, txtP2, txtP3, txtP4, txtP5 };

            for (int i = 0; i < parms.Length && i < 5; i++)
            {
                labels[i].Text = parms[i].Label;
                boxes[i].Text = parms[i].Default;
                boxes[i].Visibility = Visibility.Visible;
            }
        }

        private void HideAllParams()
        {
            txtP1.Visibility = txtP2.Visibility = txtP3.Visibility =
                txtP4.Visibility = txtP5.Visibility = Visibility.Collapsed;
            lblP1.Text = lblP2.Text = lblP3.Text = lblP4.Text = lblP5.Text = "";
        }

        private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (lstMethods.SelectedItem == null) return;

            string cat = (cmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            string method = lstMethods.SelectedItem.ToString();

            if (!_methods.TryGetValue(cat, out var list)) return;
            var info = list.Find(m => m.Name == method);
            if (info == null) return;

            // Update defaults from UI
            var boxes = new[] { txtP1, txtP2, txtP3, txtP4, txtP5 };
            for (int i = 0; i < info.Params.Length && i < 5; i++)
                info.Params[i] = new ParamDef(info.Params[i].Label, boxes[i].Text);

            txtCode.Text = GenerateCode(cat, info);
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtCode.Text))
            {
                Clipboard.SetText(txtCode.Text);
                AddLog("Code copied to clipboard.");
            }
        }

        private string GenerateCode(string category, MethodInfo info)
        {
            var p = info.Params;
            string P(int i) => i < p.Length ? p[i].Default : "";

            return (category, info.Name) switch
            {
                // Connection
                ("Connection", "ConnectAsync") => Wrap($@"var mount = new NaritMountController();
bool connected = await mount.ConnectAsync();
Console.WriteLine(connected ? ""Connected!"" : ""Failed"");"),

                ("Connection", "Disconnect") => Wrap($@"mount.Disconnect();
Console.WriteLine(""Disconnected"");"),

                // Configuration
                ("Configuration", "SetObserverSite") => Wrap($@"// Set observer site (required for RA/Dec tracking)
var site = new ObserverSite(
    latitude:  {P(0)},   // degrees North
    longitude: {P(1)},   // degrees East
    elevation: {P(2)},   // meters above sea level
    timezoneUtcOffset: {P(3)}  // UTC+{P(3)}
);
mount.SetObserverSite(site);"),

                ("Configuration", "SetPointingModelPath") => Wrap($@"// Load pointing model for encoder correction
mount.SetPointingModelPath(@""{P(0)}"");"),

                ("Configuration", "SetNtpConfig") => Wrap($@"// Configure NTP time synchronization
mount.SetNtpConfig(new NtpConfig(""{P(0)}"", {P(1)}));

// Or use Thai NTP preset:
// mount.SetNtpConfig(NtpConfig.ThaiNtp);"),

                // Drive Control
                ("Drive Control", "Enable") => Wrap("mount.Enable();  // Enable both Az & Alt drives"),
                ("Drive Control", "Disable") => Wrap("mount.Disable(); // Disable both drives (stops tracking first)"),
                ("Drive Control", "Stop") => Wrap("mount.Stop();    // Emergency stop � all motion halts immediately"),

                // Motion
                ("Motion", "GotoPosition") => Wrap($@"// Slew to position (both axes simultaneously)
mount.GotoPosition(
    azimuthDeg:       {P(0)},   // target azimuth
    altitudeDeg:      {P(1)},   // target altitude
    velocityDegPerSec: {P(2)}   // slew speed
);"),

                ("Motion", "MoveVelocity") => Wrap($@"// Continuous velocity move (until stopped)
mount.MoveVelocity(
    azVelocityDegPerSec: {P(0)},   // azimuth velocity
    altVelocityDegPerSec: {P(1)}   // altitude velocity
);
// Call mount.Stop() to halt"),

                ("Motion", "Jog") => Wrap($@"// Jog at specified velocity
mount.Jog(
    azVelocityDegPerSec: {P(0)},
    altVelocityDegPerSec: {P(1)}
);"),

                ("Motion", "ApplyOffset") => Wrap($@"// Apply telescope offset using the same direction pad logic as the main app
mount.ApplyOffset(
    offsetType: {P(0)},
    stepSizeArcsec: {P(1)},
    direction: {P(2)}
);"),

                ("Motion", "ResetOffset") => Wrap(@"// Reset accumulated telescope offset on both axes
mount.ResetOffset();"),

                ("Motion", "AzimuthAxis.GotoPosition") => Wrap($@"// Single-axis goto (Azimuth only)
mount.AzimuthAxis.GotoPosition(
    positionDeg: {P(0)},
    velocityDegPerSec: {P(1)}
);"),

                ("Motion", "AltitudeAxis.GotoPosition") => Wrap($@"// Single-axis goto (Altitude only)
mount.AltitudeAxis.GotoPosition(
    positionDeg: {P(0)},
    velocityDegPerSec: {P(1)}
);"),

                // Tracking
                ("Tracking", "TrackObject") => Wrap($@"// Track a celestial object by RA/Dec (J2000 coordinates)
mount.TrackObject(
    raHours: {P(0)},      // Right Ascension in hours
    decDeg:  {P(1)},      // Declination in degrees
    objectName: ""{P(2)}""
);"),

                ("Tracking", "TrackSidereal") => Wrap(@"// Start sidereal tracking at current position
// (maintains current pointing while Earth rotates)
mount.TrackSidereal();"),

                ("Tracking", "TrackSatellite") => Wrap($@"// Track a satellite using TLE data
string tle1 = ""{P(0)}"";
string tle2 = ""{P(1)}"";
mount.TrackSatellite(tle1, tle2, ""{P(2)}"");"),

                ("Tracking", "StopTracking") => Wrap(@"// Stop tracking and halt both axes
mount.StopTracking();"),

                // Status
                ("Status (Polling)", "GetStatus") => Wrap(@"// Get comprehensive mount status snapshot
MountStatus status = mount.GetStatus();
Console.WriteLine($""State:   {status.State}"");
Console.WriteLine($""Enabled: {status.IsMountEnabled}"");
Console.WriteLine($""NTP:     {status.IsTimeSynchronized} ({status.NtpOffsetMs:F1}ms)"");
Console.WriteLine($""Mode:    {status.CurrentTrackingMode}"");
Console.WriteLine($""Object:  {status.CurrentTrackingObjectName}"");
Console.WriteLine($""Az Raw:  {status.Position.Azimuth_Raw_Deg:F4}�"");
Console.WriteLine($""Alt Raw: {status.Position.Altitude_Raw_Deg:F4}�"");"),

                ("Status (Polling)", "GetPosition") => Wrap(@"// Get current position snapshot
MountPosition pos = mount.GetPosition();
Console.WriteLine($""Az Raw:       {pos.Azimuth_Raw_Deg:F4}�"");
Console.WriteLine($""Alt Raw:      {pos.Altitude_Raw_Deg:F4}�"");
Console.WriteLine($""Az Corrected: {pos.Azimuth_Corrected_Deg:F4}�"");
Console.WriteLine($""Alt Corrected:{pos.Altitude_Corrected_Deg:F4}�"");"),

                ("Status (Polling)", "GetTrackingInfo") => Wrap(@"// Get tracking details
TrackingInfo info = mount.GetTrackingInfo();
Console.WriteLine($""Mode:     {info.Mode}"");
Console.WriteLine($""Object:   {info.ObjectName}"");
Console.WriteLine($""RA:       {info.TargetRA_Hours:F4}h"");
Console.WriteLine($""Dec:      {info.TargetDec_Deg:F4}�"");
Console.WriteLine($""OnTarget: {info.IsOnTarget}"");
Console.WriteLine($""Az RMS:   {info.AzimuthRms_Arcsec:F2}\"""");
Console.WriteLine($""Alt RMS:  {info.AltitudeRms_Arcsec:F2}\"""");
Console.WriteLine($""Duration: {info.Duration}"");"),

                ("Status (Polling)", "AzimuthAxis.GetState") => Wrap(@"// Get azimuth axis state
AxisState az = mount.AzimuthAxis.GetState();
Console.WriteLine($""Position:  {az.RawPosition_Deg:F4}�"");
Console.WriteLine($""Cmd Vel:   {az.CommandVelocity_DegPerSec:F4}�/s"");
Console.WriteLine($""Current:   {az.Current_IQ:F3} A"");
Console.WriteLine($""Enabled:   {az.IsDriveEnabled}"");
Console.WriteLine($""Tracking:  {az.IsTracking}"");
Console.WriteLine($""RMS Error: {az.TrackingRmsError_Arcsec:F2}\"""");"),

                ("Status (Polling)", "AltitudeAxis.GetState") => Wrap(@"// Get altitude axis state
AxisState alt = mount.AltitudeAxis.GetState();
Console.WriteLine($""Position:  {alt.RawPosition_Deg:F4}�"");
Console.WriteLine($""Cmd Vel:   {alt.CommandVelocity_DegPerSec:F4}�/s"");
Console.WriteLine($""Current:   {alt.Current_IQ:F3} A"");
Console.WriteLine($""Enabled:   {alt.IsDriveEnabled}"");
Console.WriteLine($""RMS Error: {alt.TrackingRmsError_Arcsec:F2}\"""");"),

                // Events
                ("Events (Subscribe)", "PositionChanged") => Wrap(@"// Subscribe to position updates (~66 Hz)
// Fires automatically when new encoder data is polled
mount.PositionChanged += (sender, e) =>
{
    MountPosition pos = e.Position;
    Console.WriteLine($""Az={pos.Azimuth_Corrected_Deg:F4}�  Alt={pos.Altitude_Corrected_Deg:F4}�"");
};"),

                ("Events (Subscribe)", "TrackingStateChanged") => Wrap(@"// Subscribe to tracking state changes
// Fires when tracking starts, stops, or mode changes
mount.TrackingStateChanged += (sender, e) =>
{
    Console.WriteLine($""Tracking: {e.IsTracking}"");
    Console.WriteLine($""Mode:     {e.Mode} (was {e.PreviousMode})"");
    Console.WriteLine($""Object:   {e.ObjectName}"");
};"),

                ("Events (Subscribe)", "ErrorOccurred") => Wrap(@"// Subscribe to mount errors
mount.ErrorOccurred += (sender, e) =>
{
    Console.WriteLine($""ERROR: {e.Message}"");
    Console.WriteLine($""Recoverable: {e.IsRecoverable}"");
    if (e.Exception != null)
        Console.WriteLine($""Exception: {e.Exception}"");
};"),

                // Full Examples
                ("Full Example", "Complete Workflow") => @"using NARIT.Mount.API;
using NARIT.Mount.API.Models;

// ???????????????????????????????????????????????????
// Complete Mount Control Workflow
// ???????????????????????????????????????????????????

// 1. Create controller
var mount = new NaritMountController();

// 2. Configure observer site (required for RA/Dec tracking)
mount.SetObserverSite(new ObserverSite(
    latitude:  18.5736,    // Doi Inthanon, Thailand
    longitude: 98.4820,
    elevation: 2457,
    timezoneUtcOffset: 7
));

// 3. Connect to drive hardware
bool connected = await mount.ConnectAsync();
if (!connected)
{
    Console.WriteLine(""Connection failed!"");
    return;
}
// 4. Enable drives
mount.Enable();

// 5. Slew to initial position
mount.GotoPosition(
    azimuthDeg: 180.0,
    altitudeDeg: 45.0,
    velocityDegPerSec: 5.0
);
await Task.Delay(10000);  // Wait for slew

// 6. Start tracking Betelgeuse
mount.TrackObject(
    raHours: 5.9195,
    decDeg: 7.4072,
    objectName: ""Betelgeuse""
);

// 7. Monitor tracking
for (int i = 0; i < 30; i++)
{
    await Task.Delay(2000);
    var info = mount.GetTrackingInfo();
    Console.WriteLine($""[{i}] OnTarget={info.IsOnTarget} "" +
        $""RMS: Az={info.AzimuthRms_Arcsec:F2}\"" Alt={info.AltitudeRms_Arcsec:F2}\"""");
}

// 8. Stop and disconnect
mount.StopTracking();
mount.Disable();
mount.Disconnect();
mount.Dispose();",

                ("Full Example", "Polling Loop") => @"using NARIT.Mount.API;
using NARIT.Mount.API.Models;

// ???????????????????????????????????????????????????
// Timer-based Polling Example
// (3rd-party app polls mount status at regular interval)
// ???????????????????????????????????????????????????

var mount = new NaritMountController();
mount.SetObserverSite(new ObserverSite(18.5736, 98.4820, 2457));
await mount.ConnectAsync();

// Create a timer to poll every 500ms (2 Hz)
var timer = new System.Timers.Timer(500);
timer.Elapsed += (s, e) =>
{
    // ??? Poll Status ???
    MountStatus status = mount.GetStatus();
    MountPosition pos  = mount.GetPosition();

    Console.WriteLine($""[{DateTime.Now:HH:mm:ss.f}] "" +
        $""State={status.State} "" +
        $""Az={pos.Azimuth_Corrected_Deg:F4}� "" +
        $""Alt={pos.Altitude_Corrected_Deg:F4}�"");

    // ??? Poll Tracking ???
    if (status.IsTracking)
    {
        TrackingInfo info = mount.GetTrackingInfo();
        Console.WriteLine($""  Tracking: {info.ObjectName} "" +
            $""OnTarget={info.IsOnTarget} "" +
            $""RMS: {info.AzimuthRms_Arcsec:F2}\"" / {info.AltitudeRms_Arcsec:F2}\"""");
    }

    // ??? Poll Per-Axis ???
    AxisState az = mount.AzimuthAxis.GetState();
    AxisState alt = mount.AltitudeAxis.GetState();
    Console.WriteLine($""  Az: vel={az.CommandVelocity_DegPerSec:F4}�/s I={az.Current_IQ:F3}A"");
    Console.WriteLine($""  Alt: vel={alt.CommandVelocity_DegPerSec:F4}�/s I={alt.Current_IQ:F3}A"");
};
timer.Start();

// ... let it run ...
await Task.Delay(30000);  // 30 seconds
timer.Stop();

mount.Disconnect();
mount.Dispose();",

                ("Full Example", "Event Subscription") => @"using NARIT.Mount.API;
using NARIT.Mount.API.Events;
using NARIT.Mount.API.Models;

// ???????????????????????????????????????????????????
// Event-based Subscription Example
// (mount pushes data to your app automatically)
// ???????????????????????????????????????????????????

var mount = new NaritMountController();
mount.SetObserverSite(new ObserverSite(18.5736, 98.4820, 2457));

// ??? Subscribe BEFORE connecting ???

// 1. Position updates (~66 Hz � high frequency!)
int posCount = 0;
mount.PositionChanged += (sender, e) =>
{
    posCount++;
    // Only log every 66th update (= ~1 Hz)
    if (posCount % 66 == 0)
    {
        Console.WriteLine($""[Position] "" +
            $""Az={e.Position.Azimuth_Corrected_Deg:F4}� "" +
            $""Alt={e.Position.Altitude_Corrected_Deg:F4}�"");
    }
};

// 2. Tracking state changes (fires only on change)
mount.TrackingStateChanged += (sender, e) =>
{
    Console.WriteLine($""[Tracking] {(e.IsTracking ? ""STARTED"" : ""STOPPED"")} "" +
        $""Mode={e.Mode} Object=\""{e.ObjectName}\"""");
};

// 3. Error events (fires only on error)
mount.ErrorOccurred += (sender, e) =>
{
    Console.WriteLine($""[ERROR] {e.Message} "" +
        $""(Recoverable={e.IsRecoverable})"");
};

// ??? Connect and use ???
await mount.ConnectAsync();
mount.Enable();

// Start tracking � TrackingStateChanged will fire
mount.TrackObject(5.9195, 7.4072, ""Betelgeuse"");

await Task.Delay(60000);  // Let events flow for 1 minute

mount.StopTracking();     // TrackingStateChanged fires again
mount.Disconnect();
mount.Dispose();",

                _ => $"// Code generation for '{info.Name}' not available yet."
            };
        }

        private static string Wrap(string body)
        {
            return $@"using NARIT.Mount.API;
using NARIT.Mount.API.Models;

// ???????????????????????????????????????????????????
{body}";
        }

        #endregion

        private OffsetType GetSelectedOffsetType()
        {
            if (cmbOffsetType?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                string.Equals(tag, "AltAzm", StringComparison.OrdinalIgnoreCase))
            {
                return OffsetType.AltAzm;
            }

            return OffsetType.RaDec;
        }

        private void UpdateOffsetDisplay(MountStatus status = null)
        {
            if (lblOffsetAxis1 == null || lblOffsetAxis2 == null ||
                valOffsetAxis1 == null || valOffsetAxis2 == null)
            {
                return;
            }

            bool isRaDec = GetSelectedOffsetType() == OffsetType.RaDec;
            lblOffsetAxis1.Text = isRaDec ? "RA" : "Azimuth";
            lblOffsetAxis2.Text = isRaDec ? "Dec" : "Altitude";

            double axis1Arcsec = status?.TelescopeOffset_Az_Arcsec ?? 0;
            double axis2Arcsec = status?.TelescopeOffset_Alt_Arcsec ?? 0;

            valOffsetAxis1.Text = $"{axis1Arcsec:F2}\"";
            valOffsetAxis2.Text = $"{axis2Arcsec:F2}\"";
        }

        #region Helpers

        private static string FormatHoursAsSexagesimal(double hours)
        {
            double normalizedHours = hours % 24.0;
            if (normalizedHours < 0)
                normalizedHours += 24.0;

            int hh = (int)Math.Floor(normalizedHours);
            double minutesTotal = (normalizedHours - hh) * 60.0;
            int mm = (int)Math.Floor(minutesTotal);
            double secondsValue = (minutesTotal - mm) * 60.0;
            int ss = (int)Math.Round(secondsValue, MidpointRounding.AwayFromZero);

            if (ss == 60)
            {
                ss = 0;
                mm++;
            }

            if (mm == 60)
            {
                mm = 0;
                hh = (hh + 1) % 24;
            }

            return $"{hh:00}:{mm:00}:{ss:00}";
        }

        private static string FormatDegreesAsSexagesimal(double degrees)
        {
            string sign = degrees < 0 ? "-" : "+";
            double absoluteDegrees = Math.Abs(degrees);
            int dd = (int)Math.Floor(absoluteDegrees);
            double minutesTotal = (absoluteDegrees - dd) * 60.0;
            int mm = (int)Math.Floor(minutesTotal);
            double secondsValue = (minutesTotal - mm) * 60.0;
            int ss = (int)Math.Round(secondsValue, MidpointRounding.AwayFromZero);

            if (ss == 60)
            {
                ss = 0;
                mm++;
            }

            if (mm == 60)
            {
                mm = 0;
                dd++;
            }

            return $"{sign}{dd:00}:{mm:00}:{ss:00}";
        }

        private void ApplyCurrentObserverSiteFromInputs()
        {
            var site = new ObserverSite(
                double.Parse(txtLat.Text),
                double.Parse(txtLon.Text),
                double.Parse(txtElev.Text),
                double.Parse(txtUtcOffset.Text));

            _mount.SetObserverSite(site);
        }

        private void ReplaceMountController(NaritMountController controller)
        {
            if (_mount != null)
            {
                try
                {
                    _mount.PositionChanged -= OnPositionChanged;
                    _mount.TrackingStateChanged -= OnTrackingStateChanged;
                    _mount.ErrorOccurred -= OnErrorOccurred;
                    _mount.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors while replacing controller instance.
                }
            }

            _mount = controller;
            _mount.PositionChanged += OnPositionChanged;
            _mount.TrackingStateChanged += OnTrackingStateChanged;
            _mount.ErrorOccurred += OnErrorOccurred;
            SyncConnectionStateFromController();
        }

        private bool SyncConnectionStateFromController()
        {
            bool actualConnected = _mount?.IsConnected == true;

            if (_isConnected != actualConnected)
            {
                _isConnected = actualConnected;
                SetConnectionUI(actualConnected);

                if (!actualConnected)
                {
                    _refreshTimer?.Stop();
                    chkAutoRefresh.IsChecked = false;
                }
            }

            return actualConnected;
        }

        private void LogMountDiagnostics(string context)
        {
            if (_mount == null || !SyncConnectionStateFromController())
                return;

            try
            {
                MountStatus status = _mount.GetStatus();
                MountPosition position = status.Position ?? _mount.GetPosition();
                string activeModelPath = _mount.GetPointingModelPath();

                double correctedMinusRawAzDeg = position.Azimuth_Corrected_Deg - position.Azimuth_Raw_Deg;
                double correctedMinusRawAltDeg = position.Altitude_Corrected_Deg - position.Altitude_Raw_Deg;

                AddLog(
                    $"[{context}] Drive enabled={status.IsMountEnabled}, " +
                    $"Offset(Az,Alt)=({status.TelescopeOffset_Az_Arcsec:F2}\", {status.TelescopeOffset_Alt_Arcsec:F2}\"), " +
                    $"Model=\"{(string.IsNullOrWhiteSpace(activeModelPath) ? "(none)" : activeModelPath)}\"");

                AddLog(
                    $"[{context}] Corrected-Raw delta -> dAz={correctedMinusRawAzDeg:F4} deg, " +
                    $"dAlt={correctedMinusRawAltDeg:F4} deg");

            if (status.CurrentTrackingMode != TrackingModeType.None || status.IsTracking)
            {
                TrackingInfo tracking = _mount.GetTrackingInfo();
                AddLog(
                    $"[{context}] Target transform -> RA={tracking.TargetRA_Hours:F6} h, " +
                    $"Dec={tracking.TargetDec_Deg:F6} deg => Az={tracking.TargetAzimuth_Deg:F4} deg, " +
                    $"Alt={tracking.TargetAltitude_Deg:F4} deg");

                if (dgBrightStars?.SelectedItem is BrightStarRow selectedStar)
                {
                    AddLog(
                        $"[{context}] Table-vs-API target delta -> " +
                        $"dAz={NormalizeSignedAngleDegrees(tracking.TargetAzimuth_Deg - selectedStar.AzimuthDegrees):F4} deg, " +
                        $"dAlt={tracking.TargetAltitude_Deg - selectedStar.AltitudeDegrees:F4} deg");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog($"? {context} diagnostics: {ex.Message}");
            }
        }

        private void InitializeBrightStarCatalog()
        {
            try
            {
                _allBrightStars.Clear();

                foreach (var star in BrightStarCatalog.GetCatalog()
                    .Select(catalogStar => new BrightStarRow(
                        GetDisplayName(catalogStar),
                        catalogStar.RA,
                        catalogStar.Dec,
                        catalogStar.Magnitude))
                    .Where(star => !string.IsNullOrWhiteSpace(star.Name))
                    .GroupBy(star => star.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(star => star.Magnitude).First())
                    .OrderBy(star => star.Magnitude)
                    .ThenBy(star => star.Name)
                    .Take(200))
                {
                    _allBrightStars.Add(star);
                }
            }
            catch (Exception ex)
            {
                AddLog($"? Load bright star catalog: {ex.Message}");
            }
        }

        private void RefreshBrightStarCatalog(bool force = false)
        {
            if (!force && (DateTime.UtcNow - _lastBrightStarRefreshUtc) < TimeSpan.FromSeconds(1))
                return;

            if (!TryGetObserverSiteFromUi(out var observerSite))
                return;

            string previouslySelectedName = (dgBrightStars?.SelectedItem as BrightStarRow)?.Name;
            _lastBrightStarRefreshUtc = DateTime.UtcNow;

            foreach (var star in _allBrightStars)
            {
                UpdateBrightStarPosition(star, observerSite, _lastBrightStarRefreshUtc);
            }

            IEnumerable<BrightStarRow> orderedStars = GetSortedBrightStars();

            _brightStars.Clear();
            foreach (var star in orderedStars)
            {
                _brightStars.Add(star);
            }

            if (!string.IsNullOrWhiteSpace(previouslySelectedName))
            {
                var selected = _brightStars.FirstOrDefault(star =>
                    string.Equals(star.Name, previouslySelectedName, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                    dgBrightStars.SelectedItem = selected;
            }
            else if (dgBrightStars != null && dgBrightStars.SelectedItem == null && _brightStars.Count > 0)
            {
                dgBrightStars.SelectedIndex = 0;
            }

            UpdateSelectedStarInfo();
        }

        private IEnumerable<BrightStarRow> GetSortedBrightStars()
        {
            if (_trackColumnSortDirection == ListSortDirection.Ascending)
            {
                return _allBrightStars
                    .OrderBy(star => star.IsTrackable)
                    .ThenBy(star => star.AltitudeDegrees)
                    .ThenBy(star => star.Magnitude)
                    .ThenBy(star => star.Name);
            }

            return _allBrightStars
                .OrderByDescending(star => star.IsTrackable)
                .ThenByDescending(star => star.AltitudeDegrees)
                .ThenBy(star => star.Magnitude)
                .ThenBy(star => star.Name);
        }

        private void UpdateBrightStarPosition(BrightStarRow star, ObserverSite observerSite, DateTime utcNow)
        {
            var site = new LatLon(
                Angle.FromDegs(observerSite.Latitude),
                Angle.FromDegs(observerSite.Longitude));
            double julianDay = AstroTime.JulianDay(utcNow);

            var raDecJ2000 = RaDec.FromHoursDegs(star.RightAscensionHours, star.DeclinationDegrees);
            var raDecApparent = raDecJ2000.J2000ToNow();
            var altAz = raDecApparent.ToAltAz(site, julianDay);

            star.AzimuthDegrees = altAz.Az.Degs;
            star.AltitudeDegrees = altAz.Alt.Degs;
            star.IsTrackable = star.AltitudeDegrees > 0.0;
        }

        private void UpdateSelectedStarInfo()
        {
            if (txtSelectedStarInfo == null)
                return;

            if (dgBrightStars?.SelectedItem is not BrightStarRow selectedStar)
            {
                txtSelectedStarInfo.Text = "Select a bright star to track.";
                return;
            }

            string availability = selectedStar.IsTrackable ? "Trackable" : "Below horizon";
            txtSelectedStarInfo.Text =
                $"{selectedStar.Name}  |  Alt {selectedStar.AltitudeDegrees:F1}°  Az {selectedStar.AzimuthDegrees:F1}°  |  {availability}";
        }

        private static string GetDisplayName(BrightStar catalogStar)
        {
            if (!string.IsNullOrWhiteSpace(catalogStar.Name))
                return catalogStar.Name.Trim();

            if (!string.IsNullOrWhiteSpace(catalogStar.Bayer))
                return catalogStar.Bayer.Trim();

            return catalogStar.HD > 0 ? $"HD {catalogStar.HD}" : $"Star {catalogStar.Id}";
        }

        private bool TryGetObserverSiteFromUi(out ObserverSite observerSite)
        {
            observerSite = null;

            if (!double.TryParse(txtLat.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
                !double.TryParse(txtLon.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude) ||
                !double.TryParse(txtElev.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double elevation) ||
                !double.TryParse(txtUtcOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double utcOffset))
            {
                return false;
            }

            observerSite = new ObserverSite(latitude, longitude, elevation, utcOffset);
            return true;
        }

        private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static double NormalizeSignedAngleDegrees(double angleDegrees)
        {
            double normalized = angleDegrees % 360.0;
            if (normalized > 180.0)
                normalized -= 360.0;
            if (normalized < -180.0)
                normalized += 360.0;
            return normalized;
        }

        private bool EnsureConnected()
        {
            if (_mount != null && SyncConnectionStateFromController()) return true;
            AddLog("? Not connected. Click 'Connect' first.");
            return false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer?.Stop();
            try
            {
                if (_mount != null)
                {
                    _mount.PositionChanged -= OnPositionChanged;
                    _mount.TrackingStateChanged -= OnTrackingStateChanged;
                    _mount.ErrorOccurred -= OnErrorOccurred;
                    _mount.Dispose();
                }
            }
            catch { }
            base.OnClosed(e);
        }

        #endregion

        #region Data Classes

        private record ParamDef(string Label, string Default);

        private class MethodInfo
        {
            public string Name { get; }
            public string Description { get; }
            public ParamDef[] Params { get; set; }

            public MethodInfo(string name, string desc, ParamDef[] parms)
            {
                Name = name;
                Description = desc;
                Params = parms;
            }
        }

        private sealed class BrightStarRow : INotifyPropertyChanged
        {
            private double _altitudeDegrees;
            private double _azimuthDegrees;
            private bool _isTrackable;

            public BrightStarRow(string name, double rightAscensionHours, double declinationDegrees, double magnitude)
            {
                Name = name;
                RightAscensionHours = rightAscensionHours;
                DeclinationDegrees = declinationDegrees;
                Magnitude = magnitude;
            }

            public string Name { get; }
            public double RightAscensionHours { get; }
            public double DeclinationDegrees { get; }
            public double Magnitude { get; }

            public double AltitudeDegrees
            {
                get => _altitudeDegrees;
                set
                {
                    if (Math.Abs(_altitudeDegrees - value) < 0.0001) return;
                    _altitudeDegrees = value;
                    OnPropertyChanged(nameof(AltitudeDegrees));
                }
            }

            public double AzimuthDegrees
            {
                get => _azimuthDegrees;
                set
                {
                    if (Math.Abs(_azimuthDegrees - value) < 0.0001) return;
                    _azimuthDegrees = value;
                    OnPropertyChanged(nameof(AzimuthDegrees));
                }
            }

            public bool IsTrackable
            {
                get => _isTrackable;
                set
                {
                    if (_isTrackable == value) return;
                    _isTrackable = value;
                    OnPropertyChanged(nameof(IsTrackable));
                    OnPropertyChanged(nameof(AvailabilityBrush));
                }
            }

            public Brush AvailabilityBrush => IsTrackable ? TrackableBrush : NotTrackableBrush;

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        #endregion
    }
}
