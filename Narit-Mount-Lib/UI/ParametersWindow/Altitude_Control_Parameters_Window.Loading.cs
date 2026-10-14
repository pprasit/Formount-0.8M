using System;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Altitude_Control_Parameters_Window - Loading Parameters
    /// Handles loading parameters from Drive for Axis 2 (Altitude)
    /// </summary>
    public partial class Altitude_Control_Parameters_Window
    {
        /// <summary>
        /// Read all parameters directly from Drive (Axis 2)
        /// </summary>
        private void LoadParametersFromDrive()
        {
            if (_platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog("[LOAD] Drive not connected - Cannot read parameters from Drive (Axis 2)", LogType.Error);
                LoadDefaultParameters();
                return;
            }

            try
            {
                const int TIMEOUT_MS = 1000;

                AddLog("[LOAD] Reading all parameters from Drive (Axis 2)...", LogType.Message);

                // ========== 1. PD Controller Parameters (Read from Config - not stored in Drive) ==========
                var pdParams = AppSettings.GetPDControllerParameters();
                txtKp.Text = FormatToDecimal(pdParams.Kp.ToString());
                txtKi.Text = FormatToDecimal(pdParams.Ki.ToString());
                txtKd.Text = FormatToDecimal(pdParams.Kd.ToString());
                txtDeadband.Text = FormatToDecimal(pdParams.Deadband.ToString());
                AddLog($"[LOAD] PD Controller loaded from config: Kp={pdParams.Kp:F4}, Ki={pdParams.Ki:F4}, Kd={pdParams.Kd:F4}", LogType.Message);

                // ========== 2. Motion Parameters from Config ==========
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                txtAcceleration.Text = FormatToDecimal(axisConfig.Acceleration_Deg_Sec2.ToString());
                txtDeceleration.Text = FormatToDecimal(axisConfig.Deceleration_Deg_Sec2.ToString());
                txtStopDeceleration.Text = FormatToDecimal(axisConfig.Stop_Deceleration_Deg_Sec2.ToString());
                AddLog($"[LOAD] Motion Parameters loaded: ACC={axisConfig.Acceleration_Deg_Sec2:F2}, DCC={axisConfig.Deceleration_Deg_Sec2:F2}, Stop DCC={axisConfig.Stop_Deceleration_Deg_Sec2:F2}, Smooth={axisConfig.Smooth_Factor_Ms:F1}ms", LogType.Message);

                // ========== 3. Elmo Internal Loop Parameters - Read directly from Drive (Axis 2) ==========
                
                // KP[2] = Velocity Gain [A/(cnt/sec)] -> Convert to [A/deg/sec]
                if (!LoadVelocityGain(TIMEOUT_MS))
                {
                    txtVelocityGain.Text = "0.000001";
                    AddLog("[LOAD] Warning: Using default Velocity Gain: 0.000001 A/deg/sec (Axis 2)", LogType.Error);
                }

                // KI[2] = Velocity Integral [Hz]
                if (!LoadVelocityIntegral(TIMEOUT_MS))
                {
                    txtVelocityIntegral.Text = "0.01";
                    AddLog("[LOAD] Warning: Using default Velocity Integral: 0.01 Hz (Axis 2)", LogType.Error);
                }

                // KP[3] = Position Gain [rad/sec]
                if (!LoadPositionGain(TIMEOUT_MS))
                {
                    txtPositionGain.Text = "1";
                    AddLog("[LOAD] Warning: Using default Position Gain: 1 rad/sec (Axis 2)", LogType.Error);
                }

                // FF[1] = Acc Feed Forward
                // Drive stores a raw scaled value, UI shows A/deg/sec^2
                if (!LoadAccFeedForward(TIMEOUT_MS))
                {
                    txtAccFeedForward.Text = "0";
                    AddLog("[LOAD] Warning: Using default Acc Feed Forward: 0 A/deg/sec^2 (Axis 2)", LogType.Error);
                }

                // FF[2] = Velocity Feed Forward
                if (!LoadVelFeedForward(TIMEOUT_MS))
                {
                    txtVelFeedForward.Text = "1";
                    AddLog("[LOAD] Warning: Using default Vel Feed Forward: 1 (Axis 2)", LogType.Error);
                }

                // ========== 4. Filter Parameters (Axis 2) ==========
                LoadFilterParameters();

                AddLog("[LOAD] SUCCESS: All parameters loaded from Drive (Axis 2) successfully", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[LOAD] ERROR: Failed to load parameters (Axis 2): {ex.Message}", LogType.Error);
                LoadDefaultParameters();
            }
        }

        #region Individual Parameter Loading (Axis 2)

        private bool LoadVelocityGain(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KP[2]", out response, timeout))
                {
                    AddLog($"[READ] {_axisPrefix}.KP[2] raw response from Drive: '{response}'", LogType.Message);
                    
                    if (double.TryParse(response.Trim(), out double kp2_drive))
                    {
                        double velocityGain_UI = kp2_drive * VELOCITY_GAIN_CONVERSION_FACTOR;
                        txtVelocityGain.Text = FormatToDecimal(velocityGain_UI.ToString());
                        AddLog($"[READ] SUCCESS: Velocity Gain (KP[2]) = {velocityGain_UI:F6} A/(deg/sec) (Drive value: {kp2_drive:E6} A/(cnt/sec)) [Axis 2]", LogType.Message);
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse {_axisPrefix}.KP[2] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for {_axisPrefix}.KP[2]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading {_axisPrefix}.KI[2]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadVelocityIntegral(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KI[2]", out response, timeout))
                {
                    AddLog($"[READ] {_axisPrefix}.KI[2] raw response from Drive: '{response}'", LogType.Message);
                    
                    if (double.TryParse(response.Trim(), out double velInt))
                    {
                        txtVelocityIntegral.Text = FormatToDecimal(velInt.ToString());
                        AddLog($"[READ] SUCCESS: Velocity Integral (KI[2]) = {velInt:F6} Hz [Axis 2]", LogType.Message);
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse {_axisPrefix}.KI[2] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for {_axisPrefix}.KI[2]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading {_axisPrefix}.KI[2]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadPositionGain(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KP[3]", out response, timeout))
                {
                    AddLog($"[READ] {_axisPrefix}.KP[3] raw response from Drive: '{response}'", LogType.Message);
                    
                    if (double.TryParse(response.Trim(), out double posGain))
                    {
                        txtPositionGain.Text = FormatToDecimal(posGain.ToString());
                        AddLog($"[READ] SUCCESS: Position Gain (KP[3]) = {posGain:F6} rad/sec [Axis 2]", LogType.Message);
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse {_axisPrefix}.KP[3] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for {_axisPrefix}.KP[3]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading {_axisPrefix}.KP[3]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadAccFeedForward(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.FF[1]", out response, timeout))
                {
                    AddLog($"[READ] {_axisPrefix}.FF[1] raw response from Drive: '{response}'", LogType.Message);
                    
                    if (double.TryParse(response.Trim(), out double accFFDriveRaw))
                    {
                        double accFFUi = ConvertAccFeedForwardDriveToUi(accFFDriveRaw);
                        txtAccFeedForward.Text = FormatToDecimal(accFFUi.ToString());
                        AddLog($"[READ] SUCCESS: Acc Feed Forward (FF[1]) = {accFFUi:F6} A/deg/sec^2 [Axis 2] (Drive raw: {accFFDriveRaw:F4})", LogType.Message);
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse {_axisPrefix}.FF[1] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for {_axisPrefix}.FF[1]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading {_axisPrefix}.FF[1]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadVelFeedForward(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.FF[2]", out response, timeout))
                {
                    AddLog($"[READ] {_axisPrefix}.FF[2] raw response from Drive: '{response}'", LogType.Message);
                    
                    if (double.TryParse(response.Trim(), out double velFF))
                    {
                        txtVelFeedForward.Text = FormatToDecimal(velFF.ToString());
                        AddLog($"[READ] SUCCESS: Vel Feed Forward (FF[2]) = {velFF:F6} [Axis 2]", LogType.Message);
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse {_axisPrefix}.FF[2] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for {_axisPrefix}.FF[2]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading {_axisPrefix}.FF[2]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private void LoadFilterParameters()
        {
            try
            {
                AddLog("[FILTER] Reading filter parameters from Drive (Axis 2)...", LogType.Message);
                
                const int TIMEOUT_MS = 500;
                string response;
                
                // Create AltitudeInfo to store all filter data
                var info = new AltitudeInfo();
                
                // Filter #1 (Lead-Lag or Low-Pass)
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[5]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int filter1Type))
                    {
                        info.Filter1_Type = filter1Type;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[1]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double freq))
                    {
                        info.Filter1_Frequency = freq;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[2]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double damp))
                    {
                        info.Filter1_Damping = damp;
                    }
                }
                
                // Filter #2 (Notch)
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[10]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int filter2Type))
                    {
                        info.Filter2_Type = filter2Type;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[6]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double freq))
                    {
                        info.Filter2_Frequency = freq;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[7]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double q))
                    {
                        info.Filter2_QFactor = q;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[8]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double mag))
                    {
                        info.Filter2_Magnitude = mag;
                    }
                }
                
                // Filter #3 (Notch)
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[15]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int filter3Type))
                    {
                        info.Filter3_Type = filter3Type;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[11]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double freq))
                    {
                        info.Filter3_Frequency = freq;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[12]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double q))
                    {
                        info.Filter3_QFactor = q;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[13]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double mag))
                    {
                        info.Filter3_Magnitude = mag;
                    }
                }
                
                // Filter #4 (Notch)
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[20]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int filter4Type))
                    {
                        info.Filter4_Type = filter4Type;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[16]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double freq))
                    {
                        info.Filter4_Frequency = freq;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[17]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double q))
                    {
                        info.Filter4_QFactor = q;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[18]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double mag))
                    {
                        info.Filter4_Magnitude = mag;
                    }
                }
                
                // Feedback Filter #1 (Notch)
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[55]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int fbFilter1Type))
                    {
                        info.FeedbackFilter1_Type = fbFilter1Type;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[51]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double freq))
                    {
                        info.FeedbackFilter1_Frequency = freq;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[52]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double q))
                    {
                        info.FeedbackFilter1_QFactor = q;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[53]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double mag))
                    {
                        info.FeedbackFilter1_Magnitude = mag;
                    }
                }
                
                // Feedback Filter #2 (Notch)
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[60]", out response, TIMEOUT_MS))
                {
                    if (int.TryParse(response.Trim(), out int fbFilter2Type))
                    {
                        info.FeedbackFilter2_Type = fbFilter2Type;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[56]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double freq))
                    {
                        info.FeedbackFilter2_Frequency = freq;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[57]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double q))
                    {
                        info.FeedbackFilter2_QFactor = q;
                    }
                }
                
                if (_platinumDrive.SendRawCommand($"{_axisPrefix}.KV[58]", out response, TIMEOUT_MS))
                {
                    if (double.TryParse(response.Trim(), out double mag))
                    {
                        info.FeedbackFilter2_Magnitude = mag;
                    }
                }
                
                // Log summary
                AddLog($"[FILTER] SUCCESS: Filter parameters retrieved from Drive (Axis 2):", LogType.Message);
                AddLog($"[FILTER]   Filter #1: Type={info.Filter1_Type}, Freq={info.Filter1_Frequency} Hz, Damp={info.Filter1_Damping}", LogType.Message);
                AddLog($"[FILTER]   Filter #2: Type={info.Filter2_Type}, Freq={info.Filter2_Frequency} Hz", LogType.Message);
                AddLog($"[FILTER]   Filter #3: Type={info.Filter3_Type}, Freq={info.Filter3_Frequency} Hz", LogType.Message);
                AddLog($"[FILTER]   Filter #4: Type={info.Filter4_Type}, Freq={info.Filter4_Frequency} Hz", LogType.Message);
                AddLog($"[FILTER]   Feedback Filter #1: Type={info.FeedbackFilter1_Type}, Freq={info.FeedbackFilter1_Frequency} Hz", LogType.Message);
                AddLog($"[FILTER]   Feedback Filter #2: Type={info.FeedbackFilter2_Type}, Freq={info.FeedbackFilter2_Frequency} Hz", LogType.Message);
                
                // Update UI with all filter data at once
                UpdateFilterParametersUI(info);
                AddLog("[FILTER] Filter parameters updated to UI successfully (Axis 2)", LogType.Message);
            }
            catch (Exception filterEx)
            {
                AddLog($"[FILTER] WARNING: Could not read filter parameters (Axis 2): {filterEx.Message}", LogType.Error);
            }
        }

        #endregion

        /// <summary>
        /// Load default parameter values when Drive connection fails
        /// </summary>
        private void LoadDefaultParameters()
        {
            try
            {
                AddLog("[DEFAULT] Loading default parameter values (Axis 2)...", LogType.Message);
                
                // PD Controller Parameters
                var pdParams = AppSettings.GetPDControllerParameters();
                txtKp.Text = FormatToDecimal(pdParams.Kp.ToString());
                txtKi.Text = FormatToDecimal(pdParams.Ki.ToString());
                txtKd.Text = FormatToDecimal(pdParams.Kd.ToString());
                txtDeadband.Text = FormatToDecimal(pdParams.Deadband.ToString());

                // Elmo Internal Loop Parameters - Set safe defaults
                txtVelocityGain.Text = "0.000001";
                txtVelocityIntegral.Text = "0.01";
                txtPositionGain.Text = "1";
                txtAccFeedForward.Text = "0";
                txtVelFeedForward.Text = "1";
                
                // Motion Parameters - Set safe defaults (from AppSettings)
                var axisConfig = AppSettings.GetAltitudeAxisConfig();
                txtAcceleration.Text = axisConfig.Acceleration_Deg_Sec2.ToString("F2");
                txtDeceleration.Text = axisConfig.Deceleration_Deg_Sec2.ToString("F2");
                txtStopDeceleration.Text = axisConfig.Stop_Deceleration_Deg_Sec2.ToString("F2");
                
                AddLog("[DEFAULT] Default parameters loaded successfully (Axis 2)", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[DEFAULT] ERROR: Failed to load default parameters (Axis 2): {ex.Message}", LogType.Error);
            }
        }
    }
}
