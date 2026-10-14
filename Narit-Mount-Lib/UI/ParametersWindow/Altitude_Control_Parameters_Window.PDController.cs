using System;
using System.Windows;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Altitude_Control_Parameters_Window - PD Controller Parameters
    /// Handles external PID loop parameters for Altitude position control
    /// </summary>
    public partial class Altitude_Control_Parameters_Window
    {
        // Default step sizes for PD Controller
        private const double KP_STEP = 0.1;
        private const double KI_STEP = 0.01;
        private const double KD_STEP = 0.01;
        private const double DEADBAND_STEP = 0.001;
        private const double ACCEL_STEP = 10.0;
        private const double DECEL_STEP = 10.0;
        private const double STOP_DECEL_STEP = 10.0;

        #region PD Controller TextBox Events

        private void TxtKp_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtKp.Text = FormatToDecimal(txtKp.Text);
            ApplyPDParameters();
        }

        private void TxtKi_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtKi.Text = FormatToDecimal(txtKi.Text);
            ApplyPDParameters();
        }

        private void TxtKd_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtKd.Text = FormatToDecimal(txtKd.Text);
            ApplyPDParameters();
        }

        private void TxtDeadband_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtDeadband.Text = FormatToDecimal(txtDeadband.Text);
            ApplyPDParameters();
        }

        private void TxtAcceleration_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtAcceleration.Text = FormatToDecimal(txtAcceleration.Text);
            SaveMotionParameters();
        }

        private void TxtDeceleration_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtDeceleration.Text = FormatToDecimal(txtDeceleration.Text);
            SaveMotionParameters();
        }

        private void TxtStopDeceleration_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtStopDeceleration.Text = FormatToDecimal(txtStopDeceleration.Text);
            SaveMotionParameters();
        }

        #endregion

        #region PD Controller Offset Events

        private void TxtKpOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtKpOffset.Text = FormatToDecimal(txtKpOffset.Text);
            SaveOffsetsToAppSettings();
        }

        private void TxtKiOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtKiOffset.Text = FormatToDecimal(txtKiOffset.Text);
            SaveOffsetsToAppSettings();
        }

        private void TxtKdOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtKdOffset.Text = FormatToDecimal(txtKdOffset.Text);
            SaveOffsetsToAppSettings();
        }

        private void TxtDeadbandOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtDeadbandOffset.Text = FormatToDecimal(txtDeadbandOffset.Text);
            SaveOffsetsToAppSettings();
        }

        private void TxtAccelerationOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtAccelerationOffset.Text = FormatToDecimal(txtAccelerationOffset.Text);
            SaveMotionOffsetsToAppSettings();
        }

        private void TxtDecelerationOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtDecelerationOffset.Text = FormatToDecimal(txtDecelerationOffset.Text);
            SaveMotionOffsetsToAppSettings();
        }

        private void TxtStopDecelerationOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtStopDecelerationOffset.Text = FormatToDecimal(txtStopDecelerationOffset.Text);
            SaveMotionOffsetsToAppSettings();
        }

        /// <summary>
        /// Save PD Offset values to AppSettings when textbox loses focus
        /// </summary>
        private void SaveOffsetsToAppSettings()
        {
            try
            {
                var offsets = AppSettings.GetTuningOffsets();
                
                offsets.Kp_Offset = ParseDouble(txtKpOffset.Text, KP_STEP);
                offsets.Ki_Offset = ParseDouble(txtKiOffset.Text, KI_STEP);
                offsets.Kd_Offset = ParseDouble(txtKdOffset.Text, KD_STEP);
                offsets.Deadband_Offset = ParseDouble(txtDeadbandOffset.Text, DEADBAND_STEP);
                
                AppSettings.SaveTuningOffsets(offsets);
                AddLog($"[OFFSET] PD Controller offsets saved - Kp={offsets.Kp_Offset:F4}, Ki={offsets.Ki_Offset:F4}, Kd={offsets.Kd_Offset:F4}, Deadband={offsets.Deadband_Offset:F6}", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[OFFSET] ERROR: Failed to save PD offsets: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Save Motion Parameter Offset values to AppSettings
        /// </summary>
        private void SaveMotionOffsetsToAppSettings()
        {
            try
            {
                var offsets = AppSettings.GetTuningOffsets();
                
                offsets.Acceleration_Offset = ParseDouble(txtAccelerationOffset.Text, ACCEL_STEP);
                offsets.Deceleration_Offset = ParseDouble(txtDecelerationOffset.Text, DECEL_STEP);
                offsets.Stop_Deceleration_Offset = ParseDouble(txtStopDecelerationOffset.Text, STOP_DECEL_STEP);
                
                AppSettings.SaveTuningOffsets(offsets);
                AddLog($"[OFFSET] Motion Parameter offsets saved - ACC={offsets.Acceleration_Offset:F2}, DCC={offsets.Deceleration_Offset:F2}, Stop DCC={offsets.Stop_Deceleration_Offset:F2}", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[OFFSET] ERROR: Failed to save Motion offsets: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region PD Controller Up/Down Buttons

        private void BtnKpUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtKp.Text, 0);
            double offset = ParseDouble(txtKpOffset.Text, KP_STEP);
            value += offset;
            txtKp.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnKpDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtKp.Text, 0);
            double offset = ParseDouble(txtKpOffset.Text, KP_STEP);
            value -= offset;
            txtKp.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnKiUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtKi.Text, 0);
            double offset = ParseDouble(txtKiOffset.Text, KI_STEP);
            value += offset;
            txtKi.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnKiDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtKi.Text, 0);
            double offset = ParseDouble(txtKiOffset.Text, KI_STEP);
            value -= offset;
            txtKi.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnKdUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtKd.Text, 0);
            double offset = ParseDouble(txtKdOffset.Text, KD_STEP);
            value += offset;
            txtKd.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnKdDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtKd.Text, 0);
            double offset = ParseDouble(txtKdOffset.Text, KD_STEP);
            value -= offset;
            txtKd.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnDeadbandUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtDeadband.Text, 0);
            double offset = ParseDouble(txtDeadbandOffset.Text, DEADBAND_STEP);
            value += offset;
            txtDeadband.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnDeadbandDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtDeadband.Text, 0);
            double offset = ParseDouble(txtDeadbandOffset.Text, DEADBAND_STEP);
            value -= offset;
            txtDeadband.Text = FormatToDecimal(value.ToString());
            ApplyPDParameters();
        }

        private void BtnAccelerationUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtAcceleration.Text, 0);
            double offset = ParseDouble(txtAccelerationOffset.Text, ACCEL_STEP);
            value += offset;
            txtAcceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnAccelerationDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtAcceleration.Text, 0);
            double offset = ParseDouble(txtAccelerationOffset.Text, ACCEL_STEP);
            value -= offset;
            txtAcceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnDecelerationUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtDeceleration.Text, 0);
            double offset = ParseDouble(txtDecelerationOffset.Text, DECEL_STEP);
            value += offset;
            txtDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnDecelerationDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtDeceleration.Text, 0);
            double offset = ParseDouble(txtDecelerationOffset.Text, DECEL_STEP);
            value -= offset;
            txtDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnStopDecelerationUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtStopDeceleration.Text, 0);
            double offset = ParseDouble(txtStopDecelerationOffset.Text, STOP_DECEL_STEP);
            value += offset;
            txtStopDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnStopDecelerationDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtStopDeceleration.Text, 0);
            double offset = ParseDouble(txtStopDecelerationOffset.Text, STOP_DECEL_STEP);
            value -= offset;
            txtStopDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        #endregion

        #region Apply PD Parameters

        /// <summary>
        /// Apply PD Parameters immediately when changed
        /// Only saves PD Controller parameters, does NOT send motion parameters to Drive
        /// </summary>
        private void ApplyPDParameters()
        {
            try
            {
                var pdParams = new PDControllerParameters
                {
                    Kp = ParseDouble(txtKp.Text, 1.5),
                    Ki = ParseDouble(txtKi.Text, 0.005),
                    Kd = ParseDouble(txtKd.Text, 0.1),
                    Deadband = ParseDouble(txtDeadband.Text, 0.001)
                };
                
                AppSettings.SavePDControllerParameters(pdParams);
                AddLog($"[PD] Controller updated - Kp={pdParams.Kp:F4}, Ki={pdParams.Ki:F4}, Kd={pdParams.Kd:F4}, Deadband={pdParams.Deadband:F4}", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[PD] ERROR: Failed to apply PD parameters: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Save Motion Parameters to AppSettings and Drive when textbox loses focus
        /// Sends AC, DC, SD commands to Drive immediately
        /// </summary>
        private void SaveMotionParameters()
        {
            try
            {
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                
                double acceleration = ParseDouble(txtAcceleration.Text, 40.0);
                double deceleration = ParseDouble(txtDeceleration.Text, 40.0);
                double stopDeceleration = ParseDouble(txtStopDeceleration.Text, 200.0);
                
                axisConfig.Acceleration_Deg_Sec2 = acceleration;
                axisConfig.Deceleration_Deg_Sec2 = deceleration;
                axisConfig.Stop_Deceleration_Deg_Sec2 = stopDeceleration;
                
                AppSettings.SaveAltitudeAxisConfig(axisConfig);
                AddLog($"[MOTION] Parameters saved to AppSettings - ACC={acceleration:F2}, DCC={deceleration:F2}, Stop DCC={stopDeceleration:F2}", LogType.Message);
                
                // Send to Drive if connected
                if (_platinumDrive != null && _platinumDrive.IsConnected && _pollingManager != null)
                {
                    // Get Smooth Factor from AppSettings
                    double smoothFactor = axisConfig.Smooth_Factor_Ms;
                    
                    // Queue commands to send to Drive
                    AddLog($"[MOTION] Sending to Drive - ACC={acceleration:F2} deg/s², DCC={deceleration:F2} deg/s², Stop DCC={stopDeceleration:F2} deg/s², SF={smoothFactor:F1}ms", LogType.Message);
                    
                    _pollingManager.EnqueueExtraCommand($"SetMotionParams_Axis2_ACC_{acceleration:F2}", async () =>
                    {
                        try
                        {
                            // Send directly to Axis 2 using PlatinumDrive
                            await System.Threading.Tasks.Task.Run(() =>
                            {
                                string response;
                                const int AXIS = 2; // Altitude = Axis 2
                                const int TIMEOUT_MS = 2000;
                                
                                // Convert degrees/sec² to counts/sec²
                                double acCounts = (acceleration / 360.0) * axisConfig.Encoder_Resolution;
                                double dcCounts = (deceleration / 360.0) * axisConfig.Encoder_Resolution;
                                double sdCounts = (stopDeceleration / 360.0) * axisConfig.Encoder_Resolution;
                                
                                // Send commands (use AXn.command= format)
                                string acCmd = $"AX{AXIS}.AC={(int)acCounts}";
                                string dcCmd = $"AX{AXIS}.DC={(int)dcCounts}";
                                string sdCmd = $"AX{AXIS}.SD={(int)sdCounts}";
                                string sfCmd = $"AX{AXIS}.SF={(int)smoothFactor}";
                                
                                _platinumDrive.SendRawCommand(acCmd, out response, TIMEOUT_MS);
                                _platinumDrive.SendRawCommand(dcCmd, out response, TIMEOUT_MS);
                                _platinumDrive.SendRawCommand(sdCmd, out response, TIMEOUT_MS);
                                _platinumDrive.SendRawCommand(sfCmd, out response, TIMEOUT_MS);
                            });
                            
                            AddLog($"[MOTION] SUCCESS: Motion parameters sent to Drive (Axis 2)", LogType.Message);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[MOTION] ERROR: Failed to send to Drive: {ex.Message}", LogType.Error);
                            return false;
                        }
                    });
                    
                    AddLog($"[MOTION] Commands queued (Queue size: {_pollingManager.ExtraCommandQueueSize})", LogType.Message);
                }
                else
                {
                    AddLog("[MOTION] Drive not connected - Parameters saved to AppSettings only", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[MOTION] ERROR: Failed to save motion parameters: {ex.Message}", LogType.Error);
            }
        }

        #endregion
    }
}


