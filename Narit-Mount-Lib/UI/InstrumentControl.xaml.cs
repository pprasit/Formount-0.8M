using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForkMount_WPF.Engine.Manager;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Field-Derotator Control — Axis 3 (PlatinumDrive FORK1), 32-bit encoder
    /// Supports manual jog (JP+BG), goto position (MO=1→SP→PA→BG),
    /// and automatic Field-Derotation tracking based on parallactic angle rate.
    /// </summary>
    public partial class InstrumentControl : UserControl
    {
        // ── Axis constants ──────────────────────────────────────────────────────────
        private long  _instEncoderResolution = 4_294_967_296L; // loaded from AppSettings.FieldDerotator_AxisConfig
        private int   _instAxisNumber        = 3;               // loaded from AppSettings.FieldDerotator_AxisConfig

        // ── Observer site ───────────────────────────────────────────────────────────
        private double       _observerLatDeg          = 18.5736;        // loaded from AppSettings
        private const double EARTH_SIDEREAL_RAD_S    = 7.2921150e-5;   // rad/s

        // ── Dependencies ────────────────────────────────────────────────────────────
        private CentralizedPollingManager _pollingManager;

        // ── Cached poll values ───────────────────────────────────────────────────────
        private double _lastPositionDeg = double.NaN;
        private double _lastVelocityDeg = double.NaN;
        private bool   _isMotorEnabled  = false;

        // ── Field-Derotation state ───────────────────────────────────────────────────
        private bool            _isFieldDerotationEnabled = false;
        private bool            _isDerotationDirectionInverted = false;
        private DispatcherTimer _derotationTimer;
        private int             _derotationSessionId = 0;

        // ── UI refresh timer ────────────────────────────────────────────────────────
        private DispatcherTimer _uiTimer;

        public InstrumentControl()
        {
            InitializeComponent();
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Public init
        // ════════════════════════════════════════════════════════════════════════════

        public void Initialize(CentralizedPollingManager pm)
        {
            _pollingManager = pm;

            // UI / poll timer — 100 ms
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            // Derotation send timer — 500 ms (smooth enough for <0.005 deg/s rates)
            _derotationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _derotationTimer.Tick += OnDerotationTick;

            LoadSettingsAsync();
            // Load axis config from AppSettings
            var fdCfg = ForkMount_WPF.Engine.Utility.AppSettings.GetFieldDerotatorAxisConfig();
            _instEncoderResolution = fdCfg.Encoder_Resolution;
            _instAxisNumber        = fdCfg.Axis_Number;
            SimpleLogger.Info("[INST] Field-Derotator initialized (Axis 3 / FORK1, 32-bit)", "Inst");
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  UI + poll tick (100 ms)
        // ════════════════════════════════════════════════════════════════════════════

        private void OnUiTick(object sender, EventArgs e)
        {
            if (!double.IsNaN(_lastPositionDeg))
                txtInstCurrentPosition.Text = _lastPositionDeg.ToString("F3");
            if (!double.IsNaN(_lastVelocityDeg))
                txtInstCurrentVelocity.Text = _lastVelocityDeg.ToString("F4");

            var fdInfo = _pollingManager?.FieldDerotatorInfo;
            if (fdInfo != null && fdInfo.Drive_Timestamp != DateTime.MinValue)
            {
                _lastPositionDeg = fdInfo.Position_Deg;
                _lastVelocityDeg = fdInfo.Velocity_Deg_Sec;
                bool enabled = fdInfo.Is_Motor_Enabled;
                if (enabled != _isMotorEnabled)
                {
                    _isMotorEnabled = enabled;
                    Dispatcher.InvokeAsync(() => UpdateEnableButton(_isMotorEnabled));
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Field-Derotation  (parallactic angle rate from Az / Alt)
        // ════════════════════════════════════════════════════════════════════════════

        private void BtnFieldDerotation_Click(object sender, RoutedEventArgs e)
        {
            if (_isFieldDerotationEnabled)
            {
                StopFieldDerotation(sendStop: true, statusMessage: "Field-Derotation stopped");
            }
            else
            {
                _isFieldDerotationEnabled = true;
                _derotationSessionId++;
                _derotationTimer.Start();
                btnFieldDerotation.Content    = "Field-Derot: ON";
                btnFieldDerotation.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#10B981"));
                SetStatus("Field-Derotation active", "#10B981");
                SimpleLogger.Info("[INST] Field-Derotation STARTED", "Inst");
            }
        }

        /// <summary>
        /// Every 500 ms — compute parallactic angle rate from mount Az/Alt
        /// and send JP+BG to keep the field derotator in sync.
        /// The mount uses north-based azimuth (N=0°, E=90°), so the rate is
        /// dPA/dt = ω_E · cos(φ) · cos(Az) / cos(Alt).
        /// </summary>
        private void OnDerotationTick(object sender, EventArgs e)
        {
            if (!_isFieldDerotationEnabled)
                return;

            var mountInfo = _pollingManager?.MountInfo;
            if (mountInfo == null) return;

            double azDeg  = mountInfo.Act_Azimuth_Pos_Correct;
            double altDeg = mountInfo.Act_Altitude_Pos_Correct;

            // Guard against cos(Alt) → 0 near zenith
            if (Math.Abs(altDeg) > 89.5)
            {
                SetStatus("Near zenith — derotation paused", "#F59E0B");
                return;
            }

            double azRad  = azDeg  * Math.PI / 180.0;
            double altRad = altDeg * Math.PI / 180.0;
            double latRad = _observerLatDeg * Math.PI / 180.0;

            // Parallactic angle rate for north-based azimuth (N=0°, E=90°), then convert to deg/s.
            double geometricRateRadPerSec = EARTH_SIDEREAL_RAD_S * Math.Cos(latRad)
                                            * Math.Cos(azRad) / Math.Cos(altRad);
            double geometricRateDegPerSec = geometricRateRadPerSec * 180.0 / Math.PI;
            double rateDegPerSec = _isDerotationDirectionInverted
                ? -geometricRateDegPerSec
                : geometricRateDegPerSec;

            // Update shared FieldDerotatorInfo with current derotation state
            _pollingManager?.FieldDerotatorInfo?.UpdateDerotation(true, rateDegPerSec, azDeg, altDeg);

            // Update display
            txtDerotAz.Text           = $"{azDeg:F3}°";
            txtDerotAlt.Text          = $"{altDeg:F3}°";
            txtDerotComputedRate.Text = $"{rateDegPerSec:F6} °/s";

            // Send jog command
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected || !_isMotorEnabled) return;

            long velCounts = (long)(rateDegPerSec / 360.0 * _instEncoderResolution);
            string jpCmd   = $"AX{_instAxisNumber}.JP={velCounts}";
            string bgCmd   = $"AX{_instAxisNumber}.BG";
            int sessionId  = _derotationSessionId;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (!_isFieldDerotationEnabled || sessionId != _derotationSessionId)
                        return;

                    string resp;
                    drive.SendRawCommand($"AX{_instAxisNumber}.MO=1", out resp, 300);

                    if (!_isFieldDerotationEnabled || sessionId != _derotationSessionId)
                        return;

                    drive.SendRawCommand(jpCmd, out resp, 300);
                    drive.SendRawCommand(bgCmd, out resp, 300);

                    Dispatcher.InvokeAsync(() =>
                    {
                        ShowLastCommand($"{jpCmd}  {bgCmd}");
                        SetStatus($"Derotating  {rateDegPerSec:+0.000000;-0.000000} °/s  "
                                + $"Az={azDeg:F1}°  Alt={altDeg:F1}°", "#10B981");
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"[INST] Derotation send error: {ex.Message}", "Inst");
                }
            });
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Enable / Disable
        // ════════════════════════════════════════════════════════════════════════════

        private void BtnInstEnable_Click(object sender, RoutedEventArgs e)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) return;

            bool target = !_isMotorEnabled;
            if (!target && _isFieldDerotationEnabled)
                StopFieldDerotation(sendStop: true, statusMessage: "Field-Derotation stopped before disabling motor");
            string cmd  = $"AX{_instAxisNumber}.MO={(target ? 1 : 0)}";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    bool ok = drive.SendRawCommand(cmd, out resp, 1000);
                    if (ok) _isMotorEnabled = target;

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ok)
                        {
                            UpdateEnableButton(_isMotorEnabled);
                            SetStatus(_isMotorEnabled ? "Motor enabled" : "Motor disabled",
                                      _isMotorEnabled ? "#10B981" : "#F59E0B");
                        }
                        else SetStatus("Command failed — check drive", "#EF4444");
                        ShowLastCommand(cmd);
                    });
                }
                catch (Exception ex) { SimpleLogger.Error($"[INST] Enable error: {ex.Message}", "Inst"); }
            });
        }

        private void UpdateEnableButton(bool isEnabled)
        {
            if (btnInstEnable == null) return;
            string col = isEnabled ? "#EF4444" : "#10B981";
            btnInstEnable.Content    = isEnabled ? "Disable" : "Enable";
            btnInstEnable.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(col));

            instMotorIndicator.Fill  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                isEnabled ? "#10B981" : "#EF4444"));
            txtInstMotorStatus.Text       = isEnabled ? "Enabled"  : "Disabled";
            txtInstMotorStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                isEnabled ? "#10B981" : "#EF4444"));
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Velocity Jog  (JP + BG)
        // ════════════════════════════════════════════════════════════════════════════

        // ── Press-and-hold Jog handlers ─────────────────────────────────────────
        private void BtnInstJogPlus_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;   // prevent Click from also firing
            SendJog(+1);
        }
        private void BtnInstJogPlus_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            SendStop();
        }
        private void BtnInstJogMinus_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            SendJog(-1);
        }
        private void BtnInstJogMinus_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            SendStop();
        }

        private void SendJog(int direction)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) return;
            if (!double.TryParse(txtInstJogSpeed.Text, out double speedDeg) || speedDeg <= 0)
            { SetStatus("Invalid jog speed", "#EF4444"); return; }

            long speedCounts = (long)(speedDeg / 360.0 * _instEncoderResolution) * direction;
            string jpCmd = $"AX{_instAxisNumber}.JP={speedCounts}";
            string bgCmd = $"AX{_instAxisNumber}.BG";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    drive.SendRawCommand($"AX{_instAxisNumber}.MO=1", out resp, 500);
                    bool ok  = drive.SendRawCommand(jpCmd, out resp, 500);
                         ok &= drive.SendRawCommand(bgCmd, out resp, 500);

                    Dispatcher.InvokeAsync(() =>
                    {
                        ShowLastCommand($"{jpCmd}  {bgCmd}");
                        SetStatus(ok ? $"Jogging {(direction > 0 ? "+" : "-")}{speedDeg:F3} °/s"
                                     : "Jog failed", ok ? "#3B82F6" : "#EF4444");
                    });
                }
                catch (Exception ex) { SimpleLogger.Error($"[INST] Jog error: {ex.Message}", "Inst"); }
            });
        }

        private void BtnInstJogStop_Click(object sender, RoutedEventArgs e) => SendStop();

        // ════════════════════════════════════════════════════════════════════════════
        //  Goto Position  (MO=1 → SP → PA → BG)
        // ════════════════════════════════════════════════════════════════════════════

        private void BtnInstGoto_Click(object sender, RoutedEventArgs e)
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) { SetStatus("Drive not connected", "#EF4444"); return; }
            if (!double.TryParse(txtInstTargetPosition.Text, out double targetDeg))
            { SetStatus("Invalid target PA", "#EF4444"); return; }
            if (!double.TryParse(txtInstGotoVelocity.Text, out double velDeg) || velDeg <= 0)
            { SetStatus("Invalid velocity", "#EF4444"); return; }

            long targetCounts = (long)(targetDeg / 360.0 * _instEncoderResolution);
            long velCounts    = (long)(velDeg    / 360.0 * _instEncoderResolution);
            string spCmd = $"AX{_instAxisNumber}.SP={velCounts}";
            string paCmd = $"AX{_instAxisNumber}.PA={targetCounts}";
            string bgCmd = $"AX{_instAxisNumber}.BG";

            SetStatus($"Moving to PA {targetDeg:F3}° …", "#F59E0B");

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string resp;
                    bool ok  = drive.SendRawCommand($"AX{_instAxisNumber}.MO=1", out resp, 1000);
                         ok &= drive.SendRawCommand(spCmd, out resp, 1000);
                         ok &= drive.SendRawCommand(paCmd, out resp, 1000);
                         ok &= drive.SendRawCommand(bgCmd, out resp, 1000);

                    SimpleLogger.Info($"[INST] Goto PA {targetDeg:F3}° @ {velDeg:F2}°/s → {(ok?"OK":"FAIL")}", "Inst");
                    Dispatcher.InvokeAsync(() =>
                    {
                        ShowLastCommand($"{spCmd}  {paCmd}  {bgCmd}");
                        SetStatus(ok ? $"Moving → PA {targetDeg:F3}°" : "Goto failed",
                                  ok ? "#10B981" : "#EF4444");
                    });
                }
                catch (Exception ex) { SimpleLogger.Error($"[INST] Goto error: {ex.Message}", "Inst"); }
            });
        }

        private void BtnInstStop_Click(object sender, RoutedEventArgs e) => SendStop();

        // ════════════════════════════════════════════════════════════════════════════
        //  Move Continuous — toggle JP+BG at jog speed (positive direction)
        //  Press once to start, press again (or Stop) to stop.
        // ════════════════════════════════════════════════════════════════════════════

        private bool _isContinuousRunning = false;

        private void BtnInstMoveContinuous_Click(object sender, RoutedEventArgs e)
        {
            if (_isContinuousRunning)
            {
                // Currently running → Stop
                SendStop();
                _isContinuousRunning = false;
                if (btnInstMoveContinuous != null)
                {
                    btnInstMoveContinuous.Content    = "Move Continuous";
                    btnInstMoveContinuous.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8B5CF6"));
                }
            }
            else
            {
                // Not running → Start continuous jog at current jog speed (positive direction)
                var drive = _pollingManager?.PlatinumDrive;
                if (drive == null || !drive.IsConnected) return;
                if (!double.TryParse(txtInstJogSpeed.Text, out double speedDeg) || speedDeg <= 0)
                { SetStatus("Invalid jog speed", "#EF4444"); return; }

                long speedCounts = (long)(speedDeg / 360.0 * _instEncoderResolution);
                string jpCmd = $"AX{_instAxisNumber}.JP={speedCounts}";
                string bgCmd = $"AX{_instAxisNumber}.BG";

                _isContinuousRunning = true;
                if (btnInstMoveContinuous != null)
                {
                    btnInstMoveContinuous.Content    = "■ Stop Continuous";
                    btnInstMoveContinuous.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
                }

                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        string resp;
                        bool ok  = drive.SendRawCommand(jpCmd, out resp, 500);
                             ok &= drive.SendRawCommand(bgCmd, out resp, 500);
                        SimpleLogger.Info($"[INST] Move Continuous {speedDeg:F2}°/s → {(ok ? "OK" : "FAIL")}", "Inst");
                        Dispatcher.InvokeAsync(() =>
                        {
                            ShowLastCommand(jpCmd);
                            SetStatus(ok ? $"Running {speedDeg:F2}°/s continuously" : "Command failed", ok ? "#10B981" : "#EF4444");
                        });
                    }
                    catch (Exception ex) { SimpleLogger.Error($"[INST] Continuous error: {ex.Message}", "Inst"); }
                });
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Stop
        // ════════════════════════════════════════════════════════════════════════════

        private void SendStop()
        {
            if (_isFieldDerotationEnabled)
                StopFieldDerotation(sendStop: false, statusMessage: "Field-Derotation stopped");

            // Reset Continuous state and button appearance
            _isContinuousRunning = false;
            Dispatcher.InvokeAsync(() =>
            {
                if (btnInstMoveContinuous != null)
                {
                    btnInstMoveContinuous.Content    = "Move Continuous";
                    btnInstMoveContinuous.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8B5CF6"));
                }
            });

            SendStopCore();
        }

        private void StopFieldDerotation(bool sendStop, string statusMessage)
        {
            _isFieldDerotationEnabled = false;
            _derotationSessionId++;
            _derotationTimer?.Stop();

            btnFieldDerotation.Content    = "Field-Derot: OFF";
            btnFieldDerotation.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#4B5563"));

            _pollingManager?.FieldDerotatorInfo?.UpdateDerotation(false, 0, 0, 0);

            if (sendStop)
                SendStopCore();

            SetStatus(statusMessage, "#F59E0B");
            SimpleLogger.Info("[INST] Field-Derotation STOPPED", "Inst");
        }

        private void BtnDerotationDirection_Click(object sender, RoutedEventArgs e)
        {
            _isDerotationDirectionInverted = !_isDerotationDirectionInverted;
            UpdateDerotationDirectionButton();
            SaveSettingAsync(s => s.Instrument_DerotationDirectionInverted = _isDerotationDirectionInverted);

            string mode = _isDerotationDirectionInverted ? "Inverted" : "Normal";
            SetStatus($"Field-derotation direction set to {mode}", "#3B82F6");
            SimpleLogger.Info($"[INST] Field-Derotation direction set to {mode}", "Inst");
        }

        private void SendStopCore()
        {
            var drive = _pollingManager?.PlatinumDrive;
            if (drive == null || !drive.IsConnected) return;
            string stCmd = $"AX{_instAxisNumber}.ST";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    drive.SendRawCommand(stCmd, out _, 500);
                    SimpleLogger.Info("[INST] Stop sent", "Inst");
                    Dispatcher.InvokeAsync(() => { ShowLastCommand(stCmd); SetStatus("Stopped", "#F59E0B"); });
                }
                catch (Exception ex) { SimpleLogger.Error($"[INST] Stop error: {ex.Message}", "Inst"); }
            });
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Settings — MongoDB auto-save / load
        // ════════════════════════════════════════════════════════════════════════════

        private void TxtInstJogSpeed_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(txtInstJogSpeed.Text, out double v) && v > 0)
                SaveSettingAsync(s => s.Instrument_JogSpeed_Deg = v);
        }

        private void TxtInstTargetPosition_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(txtInstTargetPosition.Text, out double v))
                SaveSettingAsync(s => s.Instrument_TargetPosition_Deg = v);
        }

        private void TxtInstGotoVelocity_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(txtInstGotoVelocity.Text, out double v) && v > 0)
                SaveSettingAsync(s => s.Instrument_GotoVelocity_Deg = v);
        }

        private void TxtInst_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Keyboard.ClearFocus();
        }

        private async void SaveSettingAsync(System.Action<AppSettingsModel> patch)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var settings = AppSettings.GetAllSettings();
                    patch(settings);
                    AppSettings.SaveAllSettings(settings);
                }
                catch (Exception ex) { SimpleLogger.Warning($"[INST] Save error: {ex.Message}", "Inst"); }
            });
        }

        private async void LoadSettingsAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var s = AppSettings.GetAllSettings();
                    if (s == null) return;

                    double jog    = s.Instrument_JogSpeed_Deg    > 0 ? s.Instrument_JogSpeed_Deg    : 1.0;
                    double target = s.Instrument_TargetPosition_Deg;
                    double vel    = s.Instrument_GotoVelocity_Deg > 0 ? s.Instrument_GotoVelocity_Deg : 1.0;
                    bool invert   = s.Instrument_DerotationDirectionInverted;

                    // Load observer latitude from settings (no hard-code)
                    if (s.Observer_Location != null && s.Observer_Location.Latitude_Deg != 0)
                        _observerLatDeg = s.Observer_Location.Latitude_Deg;

                    _isDerotationDirectionInverted = invert;

                    Dispatcher.InvokeAsync(() =>
                    {
                        txtInstJogSpeed.Text       = jog.ToString("F3");
                        txtInstTargetPosition.Text = target.ToString("F3");
                        txtInstGotoVelocity.Text   = vel.ToString("F3");
                        UpdateDerotationDirectionButton();
                    });
                }
                catch (Exception ex) { SimpleLogger.Warning($"[INST] Load error: {ex.Message}", "Inst"); }
            });
        }

        // ════════════════════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════════════════════

        private void SetStatus(string message, string hexColor)
        {
            txtInstStatus.Text       = message;
            txtInstStatus.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hexColor));
        }

        private void UpdateDerotationDirectionButton()
        {
            if (btnDerotationDirection == null)
                return;

            if (_isDerotationDirectionInverted)
            {
                btnDerotationDirection.Content = "Direction: Inverted";
                btnDerotationDirection.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#F59E0B"));
            }
            else
            {
                btnDerotationDirection.Content = "Direction: Normal";
                btnDerotationDirection.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#1F2937"));
            }
        }

        private void ShowLastCommand(string cmd) => txtInstLastCommand.Text = cmd;
    }
}
