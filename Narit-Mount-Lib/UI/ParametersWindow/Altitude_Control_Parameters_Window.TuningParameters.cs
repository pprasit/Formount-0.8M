using System;
using System.Windows;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Altitude_Control_Parameters_Window - Elmo Tuning Parameters
    /// Handles Elmo internal loop parameters for Axis 2 (KP[2], KI[2], KP[3], FF[1], FF[2])
    /// </summary>
    public partial class Altitude_Control_Parameters_Window
    {
        #region Tuning Parameter TextBox Events

        private void TxtVelocityGain_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtVelocityGain.Text = FormatToDecimal(txtVelocityGain.Text);
            ApplyVelocityGain();
        }

        private void TxtVelocityIntegral_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtVelocityIntegral.Text = FormatToDecimal(txtVelocityIntegral.Text);
            ApplyVelocityIntegral();
        }

        private void TxtPositionGain_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtPositionGain.Text = FormatToDecimal(txtPositionGain.Text);
            ApplyPositionGain();
        }

        private void TxtAccFeedForward_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtAccFeedForward.Text = FormatToDecimal(txtAccFeedForward.Text);
            ApplyAccFeedForward();
        }

        private void TxtVelFeedForward_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtVelFeedForward.Text = FormatToDecimal(txtVelFeedForward.Text);
            ApplyVelFeedForward();
        }

        #endregion

        #region Tuning Parameter Offset Events

        private void TxtVelocityGainOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtVelocityGainOffset.Text = FormatToDecimal(txtVelocityGainOffset.Text);
            SaveTuningOffsetsToAppSettings();
        }

        private void TxtVelocityIntegralOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtVelocityIntegralOffset.Text = FormatToDecimal(txtVelocityIntegralOffset.Text);
            SaveTuningOffsetsToAppSettings();
        }

        private void TxtPositionGainOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtPositionGainOffset.Text = FormatToDecimal(txtPositionGainOffset.Text);
            SaveTuningOffsetsToAppSettings();
        }

        private void TxtAccFeedForwardOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtAccFeedForwardOffset.Text = FormatToDecimal(txtAccFeedForwardOffset.Text);
            SaveTuningOffsetsToAppSettings();
        }

        private void TxtVelFeedForwardOffset_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtVelFeedForwardOffset.Text = FormatToDecimal(txtVelFeedForwardOffset.Text);
            SaveTuningOffsetsToAppSettings();
        }

        private void SaveTuningOffsetsToAppSettings()
        {
            try
            {
                var offsets = AppSettings.GetTuningOffsets();
                
                offsets.Velocity_Gain_Offset = ParseDouble(txtVelocityGainOffset.Text, VEL_GAIN_STEP);
                offsets.Velocity_Integral_Offset = ParseDouble(txtVelocityIntegralOffset.Text, VEL_INT_STEP);
                offsets.Position_Gain_Offset = ParseDouble(txtPositionGainOffset.Text, POS_GAIN_STEP);
                offsets.Acc_Feed_Forward_Offset = ParseDouble(txtAccFeedForwardOffset.Text, ACC_FF_STEP);
                offsets.Vel_Feed_Forward_Offset = ParseDouble(txtVelFeedForwardOffset.Text, VEL_FF_STEP);
                
                AppSettings.SaveTuningOffsets(offsets);
                AddLog($"[OFFSET] Elmo Internal Loop offsets saved - VelGain={offsets.Velocity_Gain_Offset:E6}, VelInt={offsets.Velocity_Integral_Offset:F6}, PosGain={offsets.Position_Gain_Offset:F6}, AccFF={offsets.Acc_Feed_Forward_Offset:E6}, VelFF={offsets.Vel_Feed_Forward_Offset:F6}", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[OFFSET] ERROR: Failed to save tuning offsets: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Tuning Parameter Up/Down Buttons

        private void BtnVelocityGainUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtVelocityGain.Text, 0);
            double offset = ParseDouble(txtVelocityGainOffset.Text, VEL_GAIN_STEP);
            AddLog($"[TUNING] Velocity Gain UP: Current={value:E6}, Offset={offset:E6}", LogType.Message);
            
            if (offset == 0 || double.IsNaN(offset))
            {
                AddLog($"[TUNING] ERROR: Offset is zero or invalid!", LogType.Error);
                return;
            }
            
            value += offset;
            AddLog($"[TUNING] Velocity Gain UP: New Value={value:E6}", LogType.Message);
            txtVelocityGain.Text = FormatToDecimal(value.ToString());
            ApplyVelocityGain();
        }

        private void BtnVelocityGainDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtVelocityGain.Text, 0);
            double offset = ParseDouble(txtVelocityGainOffset.Text, VEL_GAIN_STEP);
            AddLog($"[TUNING] Velocity Gain DOWN: Current={value:E6}, Offset={offset:E6}", LogType.Message);
            
            if (offset == 0 || double.IsNaN(offset))
            {
                AddLog($"[TUNING] ERROR: Offset is zero or invalid!", LogType.Error);
                return;
            }
            
            value -= offset;
            AddLog($"[TUNING] Velocity Gain DOWN: New Value={value:E6}", LogType.Message);
            txtVelocityGain.Text = FormatToDecimal(value.ToString());
            ApplyVelocityGain();
        }

        private void BtnVelocityIntegralUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtVelocityIntegral.Text, 0);
            double offset = ParseDouble(txtVelocityIntegralOffset.Text, VEL_INT_STEP);
            value += offset;
            txtVelocityIntegral.Text = FormatToDecimal(value.ToString());
            ApplyVelocityIntegral();
        }

        private void BtnVelocityIntegralDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtVelocityIntegral.Text, 0);
            double offset = ParseDouble(txtVelocityIntegralOffset.Text, VEL_INT_STEP);
            value -= offset;
            txtVelocityIntegral.Text = FormatToDecimal(value.ToString());
            ApplyVelocityIntegral();
        }

        private void BtnPositionGainUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtPositionGain.Text, 0);
            double offset = ParseDouble(txtPositionGainOffset.Text, POS_GAIN_STEP);
            value += offset;
            txtPositionGain.Text = FormatToDecimal(value.ToString());
            ApplyPositionGain();
        }

        private void BtnPositionGainDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtPositionGain.Text, 0);
            double offset = ParseDouble(txtPositionGainOffset.Text, POS_GAIN_STEP);
            value -= offset;
            txtPositionGain.Text = FormatToDecimal(value.ToString());
            ApplyPositionGain();
        }

        private void BtnAccFeedForwardUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtAccFeedForward.Text, 0);
            double offset = ParseDouble(txtAccFeedForwardOffset.Text, ACC_FF_STEP);
            value += offset;
            txtAccFeedForward.Text = FormatToDecimal(value.ToString());
            ApplyAccFeedForward();
        }

        private void BtnAccFeedForwardDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtAccFeedForward.Text, 0);
            double offset = ParseDouble(txtAccFeedForwardOffset.Text, ACC_FF_STEP);
            value -= offset;
            txtAccFeedForward.Text = FormatToDecimal(value.ToString());
            ApplyAccFeedForward();
        }

        private void BtnVelFeedForwardUp_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtVelFeedForward.Text, 0);
            double offset = ParseDouble(txtVelFeedForwardOffset.Text, VEL_FF_STEP);
            value += offset;
            txtVelFeedForward.Text = FormatToDecimal(value.ToString());
            ApplyVelFeedForward();
        }

        private void BtnVelFeedForwardDown_Click(object sender, RoutedEventArgs e)
        {
            double value = ParseDouble(txtVelFeedForward.Text, 0);
            double offset = ParseDouble(txtVelFeedForwardOffset.Text, VEL_FF_STEP);
            value -= offset;
            txtVelFeedForward.Text = FormatToDecimal(value.ToString());
            ApplyVelFeedForward();
        }

        #endregion

        #region Apply Individual Tuning Parameters

        private void ApplyVelocityGain()
        {
            if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog("[TUNING] WARNING: Drive not connected", LogType.Warning);
                return;
            }

            try
            {
                double velocityGain_UI = ParseDouble(txtVelocityGain.Text, 0);
                double kp2_drive = velocityGain_UI / VELOCITY_GAIN_CONVERSION_FACTOR;
                
                _pollingManager.EnqueueExtraCommand($"{_axisPrefix}.KP[2]", async () =>
                {
                    string response;
                    bool success = _platinumDrive.SendRawCommand($"{_axisPrefix}.KP[2]={kp2_drive:E10}", out response);
                    if (success)
                    {
                        AddLog($"[TUNING] SUCCESS: Velocity Gain = {velocityGain_UI:F6} A/(deg/sec)", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[TUNING] ERROR: Failed to set Velocity Gain", LogType.Error);
                    }
                    return success;
                });
            }
            catch (Exception ex)
            {
                AddLog($"[TUNING] ERROR: {ex.Message}", LogType.Error);
            }
        }

        private void ApplyVelocityIntegral()
        {
            if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog("[TUNING] WARNING: Drive not connected", LogType.Warning);
                return;
            }

            try
            {
                double velocityIntegral = ParseDouble(txtVelocityIntegral.Text, 0);
                
                _pollingManager.EnqueueExtraCommand($"{_axisPrefix}.KI[2]", async () =>
                {
                    string response;
                    bool success = _platinumDrive.SendRawCommand($"{_axisPrefix}.KI[2]={velocityIntegral:E10}", out response);
                    if (success)
                    {
                        AddLog($"[TUNING] SUCCESS: Velocity Integral = {velocityIntegral:F6} Hz", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[TUNING] ERROR: Failed to set Velocity Integral", LogType.Error);
                    }
                    return success;
                });
            }
            catch (Exception ex)
            {
                AddLog($"[TUNING] ERROR: {ex.Message}", LogType.Error);
            }
        }

        private void ApplyPositionGain()
        {
            if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog("[TUNING] WARNING: Drive not connected", LogType.Warning);
                return;
            }

            try
            {
                double positionGain = ParseDouble(txtPositionGain.Text, 0);
                
                _pollingManager.EnqueueExtraCommand($"{_axisPrefix}.KP[3]", async () =>
                {
                    string response;
                    bool success = _platinumDrive.SendRawCommand($"{_axisPrefix}.KP[3]={positionGain:E10}", out response);
                    if (success)
                    {
                        AddLog($"[TUNING] SUCCESS: Position Gain = {positionGain:F6} rad/sec", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[TUNING] ERROR: Failed to set Position Gain", LogType.Error);
                    }
                    return success;
                });
            }
            catch (Exception ex)
            {
                AddLog($"[TUNING] ERROR: {ex.Message}", LogType.Error);
            }
        }

        private void ApplyAccFeedForward()
        {
            if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog("[TUNING] WARNING: Drive not connected", LogType.Warning);
                return;
            }

            try
            {
                double accFeedForwardUi = ParseDouble(txtAccFeedForward.Text, 0);
                double accFeedForwardDrive = ConvertAccFeedForwardUiToDrive(accFeedForwardUi);
                
                _pollingManager.EnqueueExtraCommand($"{_axisPrefix}.FF[1]", async () =>
                {
                    string response;
                    bool success = _platinumDrive.SendRawCommand($"{_axisPrefix}.FF[1]={accFeedForwardDrive:E10}", out response);
                    if (success)
                    {
                        AddLog($"[TUNING] SUCCESS: Acc Feed Forward = {accFeedForwardUi:F6} A/deg/sec^2 (Drive raw: {accFeedForwardDrive:F4})", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[TUNING] ERROR: Failed to set Acc Feed Forward", LogType.Error);
                    }
                    return success;
                });
            }
            catch (Exception ex)
            {
                AddLog($"[TUNING] ERROR: {ex.Message}", LogType.Error);
            }
        }

        private void ApplyVelFeedForward()
        {
            if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog("[TUNING] WARNING: Drive not connected", LogType.Warning);
                return;
            }

            try
            {
                double velFeedForward = ParseDouble(txtVelFeedForward.Text, 0);
                
                _pollingManager.EnqueueExtraCommand($"{_axisPrefix}.FF[2]", async () =>
                {
                    string response;
                    bool success = _platinumDrive.SendRawCommand($"{_axisPrefix}.FF[2]={velFeedForward:E10}", out response);
                    if (success)
                    {
                        AddLog($"[TUNING] SUCCESS: Vel Feed Forward = {velFeedForward:F6}", LogType.Message);
                    }
                    else
                    {
                        AddLog($"[TUNING] ERROR: Failed to set Vel Feed Forward", LogType.Error);
                    }
                    return success;
                });
            }
            catch (Exception ex)
            {
                AddLog($"[TUNING] ERROR: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Apply ALL Tuning Parameters (used for initialization or batch updates)
        /// </summary>
        private void ApplyTuningParameters()
        {
            ApplyVelocityGain();
            ApplyVelocityIntegral();
            ApplyPositionGain();
            ApplyAccFeedForward();
            ApplyVelFeedForward();
        }

        #endregion
    }
}
