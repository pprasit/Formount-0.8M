using System;
using System.Windows;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Azimuth_Control_Parameters_Window - PD Controller Parameters
    /// Handles PD controller parameters and their events
    /// </summary>
    public partial class Azimuth_Control_Parameters_Window
    {
        #region PD Parameter TextBox Events

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

        #endregion

        #region PD Parameter Offset Events

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
        /// Save Offset values to AppSettings when textbox loses focus
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
                AddLog("[OFFSET] PD Controller offsets saved to AppSettings");
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
                
                offsets.Acceleration_Offset = ParseDouble(txtAccelerationOffset.Text, ACC_STEP);
                offsets.Deceleration_Offset = ParseDouble(txtDecelerationOffset.Text, DCC_STEP);
                offsets.Stop_Deceleration_Offset = ParseDouble(txtStopDecelerationOffset.Text, STOP_DCC_STEP);
                
                AppSettings.SaveTuningOffsets(offsets);
                AddLog("[OFFSET] Motion Parameter offsets saved to AppSettings");
            }
            catch (Exception ex)
            {
                AddLog($"[OFFSET] ERROR: Failed to save Motion offsets: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region PD Parameter Up/Down Buttons

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

        // [REMOVED] BtnMaxVelocityUp_Click and BtnMaxVelocityDown_Click - Max Velocity now in Config tab

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
            double offset = ParseDouble(txtAccelerationOffset.Text, ACC_STEP);
            value += offset;
            txtAcceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnAccelerationDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtAcceleration.Text, 0);
            double offset = ParseDouble(txtAccelerationOffset.Text, ACC_STEP);
            value -= offset;
            txtAcceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnDecelerationUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtDeceleration.Text, 0);
            double offset = ParseDouble(txtDecelerationOffset.Text, DCC_STEP);
            value += offset;
            txtDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnDecelerationDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtDeceleration.Text, 0);
            double offset = ParseDouble(txtDecelerationOffset.Text, DCC_STEP);
            value -= offset;
            txtDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnStopDecelerationUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtStopDeceleration.Text, 0);
            double offset = ParseDouble(txtStopDecelerationOffset.Text, STOP_DCC_STEP);
            value += offset;
            txtStopDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        private void BtnStopDecelerationDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtStopDeceleration.Text, 0);
            double offset = ParseDouble(txtStopDecelerationOffset.Text, STOP_DCC_STEP);
            value -= offset;
            txtStopDeceleration.Text = FormatToDecimal(value.ToString());
            SaveMotionParameters();
        }

        #endregion

        #region Motion Parameter Events

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

        /// <summary>
        /// Save Motion Parameters to AppSettings and Drive when textbox loses focus
        /// Sends AC, DC, SD commands to Drive immediately
        /// </summary>
        private void SaveMotionParameters()
        {
            try
            {
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                
                double acceleration = ParseDouble(txtAcceleration.Text, 100.0);
                double deceleration = ParseDouble(txtDeceleration.Text, 100.0);
                double stopDeceleration = ParseDouble(txtStopDeceleration.Text, 200.0);
                
                axisConfig.Acceleration_Deg_Sec2 = acceleration;
                axisConfig.Deceleration_Deg_Sec2 = deceleration;
                axisConfig.Stop_Deceleration_Deg_Sec2 = stopDeceleration;
                
                AppSettings.SaveAzimuthAxisConfig(axisConfig);
                AddLog($"[MOTION] Parameters saved to AppSettings - ACC={acceleration:F2}, DCC={deceleration:F2}, Stop DCC={stopDeceleration:F2}");
                
                // Send to Drive if connected
                if (_azimuthDrive != null && _azimuthDrive.IsConnected && _pollingManager != null)
                {
                    // Get Smooth Factor from AppSettings
                    double smoothFactor = axisConfig.Smooth_Factor_Ms;
                    
                    // Queue commands to send to Drive
                    AddLog($"[MOTION] Sending to Drive - ACC={acceleration:F2} deg/s², DCC={deceleration:F2} deg/s², Stop DCC={stopDeceleration:F2} deg/s², SF={smoothFactor:F1}ms", LogType.Message);
                    
                    _pollingManager.EnqueueExtraCommand($"SetMotionParams_ACC_{acceleration:F2}", async () =>
                    {
                        try
                        {
                            // Use AzimuthDrive.SetMotionParameters() method
                            await System.Threading.Tasks.Task.Run(() =>
                            {
                                _azimuthDrive.SetMotionParameters(
                                    acceleration,
                                    deceleration,
                                    stopDeceleration,
                                    smoothFactor
                                );
                            });
                            
                            AddLog($"[MOTION] SUCCESS: Motion parameters sent to Drive (Axis 1)", LogType.Message);
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

        #region Apply PD Parameters

        /// <summary>
        /// Apply PD Parameters immediately when changed
        /// [UPDATED] Now updates Position Controller in real-time if active
        /// [REMOVED] MaxVelocity - now uses Max_Velocity_Deg_Sec from AxisConfig
        /// </summary>
        private void ApplyPDParameters()
        {
            try
            {
                var pdParams = new PDControllerParameters
                {
                    Kp = ParseDouble(txtKp.Text, 0),
                    Ki = ParseDouble(txtKi.Text, 0),
                    Kd = ParseDouble(txtKd.Text, 0),
                    Deadband = ParseDouble(txtDeadband.Text, 0)
                };
                
                // Save to AppSettings
                AppSettings.SaveAzimuthPDControllerParameters(pdParams);
                AddLog($"[PD] Saved to AppSettings - Kp={pdParams.Kp:F4}, Ki={pdParams.Ki:F4}, Kd={pdParams.Kd:F4}, Deadband={pdParams.Deadband:F6}");
                
                // [DEBUG] Check Position Control status
                if (_positionControl == null)
                {
                    AddLog($"[PD-DEBUG] ?? _positionControl is NULL - Cannot update real-time", LogType.Error);
                    AddLog($"[PD-DEBUG] Parameters saved to AppSettings only - Will apply on next Position Control Start", LogType.Message);
                    return;
                }
                
                AddLog($"[PD-DEBUG] _positionControl reference: {(_positionControl != null ? "? OK" : "? NULL")}", LogType.Message);
                AddLog($"[PD-DEBUG] Position Control IsActive: {_positionControl.IsActive}", LogType.Message);
                
                // [NEW] Update Position Controller in real-time if active
                if (_positionControl != null && _positionControl.IsActive)
                {
                    var axisConfig = AppSettings.GetAzimuthAxisConfig();
                    _positionControl.UpdateParameters(
                        pdParams.Kp,
                        pdParams.Ki,
                        pdParams.Kd,
                        axisConfig.Max_Velocity_Deg_Sec,
                        pdParams.Deadband
                    );
                    AddLog($"[PD] ? REAL-TIME UPDATE APPLIED!", LogType.Message);
                    AddLog($"[PD] ? Kp={pdParams.Kp:F4}, Ki={pdParams.Ki:F4}, Kd={pdParams.Kd:F4}, MaxVel={axisConfig.Max_Velocity_Deg_Sec:F2}, Deadband={pdParams.Deadband:F6}", LogType.Message);
                }
                else if (_positionControl != null && !_positionControl.IsActive)
                {
                    AddLog($"[PD-DEBUG] ?? Position Control is NOT ACTIVE - Parameters saved, will apply on next Start", LogType.Message);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[PD] ERROR: Failed to apply PD parameters: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Load PD Parameters from AppSettings (when drive not connected)
        /// [REMOVED] MaxVelocity - now uses Max_Velocity_Deg_Sec from AxisConfig
        /// [REMOVED] Smooth Factor - now managed in Config tab only
        /// </summary>
        private void LoadParametersFromSettings()
        {
            try
            {
                var pdParams = AppSettings.GetAzimuthPDControllerParameters();
                
                txtKp.Text = pdParams.Kp.ToString("F4");
                txtKi.Text = pdParams.Ki.ToString("F4");
                txtKd.Text = pdParams.Kd.ToString("F4");
                txtDeadband.Text = pdParams.Deadband.ToString("F4");
                
                AddLog($"[PD] Loaded from AppSettings - Kp={pdParams.Kp:F4}, Ki={pdParams.Ki:F4}, Kd={pdParams.Kd:F4}, Deadband={pdParams.Deadband:F4}");
                
                // Load Motion Parameters (without Smooth Factor)
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                txtAcceleration.Text = axisConfig.Acceleration_Deg_Sec2.ToString("F2");
                txtDeceleration.Text = axisConfig.Deceleration_Deg_Sec2.ToString("F2");
                txtStopDeceleration.Text = axisConfig.Stop_Deceleration_Deg_Sec2.ToString("F2");
                
                AddLog($"[MOTION] Loaded from AppSettings - ACC={axisConfig.Acceleration_Deg_Sec2:F2}, DCC={axisConfig.Deceleration_Deg_Sec2:F2}, Stop DCC={axisConfig.Stop_Deceleration_Deg_Sec2:F2}");
            }
            catch (Exception ex)
            {
                AddLog($"[PD] ERROR: Failed to load parameters from AppSettings: {ex.Message}", LogType.Error);
            }
        }

        #endregion
    }
}
