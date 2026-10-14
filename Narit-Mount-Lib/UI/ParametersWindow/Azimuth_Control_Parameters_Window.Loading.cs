using System;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Azimuth_Control_Parameters_Window - Loading Parameters
    /// Handles loading parameters from Drive and loading defaults
    /// </summary>
    public partial class Azimuth_Control_Parameters_Window
    {
        /// <summary>
        /// Read all parameters directly from Drive
        /// Does not use values from AppSetting.json anymore
        /// </summary>
        private void LoadParametersFromDrive()
        {
            if (_azimuthDrive == null || !_azimuthDrive.IsConnected)
            {
                AddLog("[LOAD] Drive not connected - Cannot read parameters from Drive", LogType.Error);
                LoadDefaultParameters();
                return;
            }

            try
            {
                const int TIMEOUT_MS = 1000;

                AddLog("[LOAD] Reading all parameters from Drive...");

                // ========== 1. PD Controller Parameters (Read from Config - not stored in Drive) ==========
                var pdParams = AppSettings.GetAzimuthPDControllerParameters();
                txtKp.Text = FormatToDecimal(pdParams.Kp.ToString());
                txtKi.Text = FormatToDecimal(pdParams.Ki.ToString());
                txtKd.Text = FormatToDecimal(pdParams.Kd.ToString());
                txtDeadband.Text = FormatToDecimal(pdParams.Deadband.ToString());
                AddLog($"[LOAD] PD Controller loaded from config: Kp={pdParams.Kp:F4}, Ki={pdParams.Ki:F4}, Kd={pdParams.Kd:F4}");

                // ========== 2. Motion Parameters from Config ==========
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                txtAcceleration.Text = FormatToDecimal(axisConfig.Acceleration_Deg_Sec2.ToString());
                txtDeceleration.Text = FormatToDecimal(axisConfig.Deceleration_Deg_Sec2.ToString());
                txtStopDeceleration.Text = FormatToDecimal(axisConfig.Stop_Deceleration_Deg_Sec2.ToString());
                AddLog($"[LOAD] Motion Parameters loaded: ACC={axisConfig.Acceleration_Deg_Sec2:F2}, DCC={axisConfig.Deceleration_Deg_Sec2:F2}, Stop DCC={axisConfig.Stop_Deceleration_Deg_Sec2:F2}, Smooth={axisConfig.Smooth_Factor_Ms:F1}ms");

                // ========== 3. Elmo Internal Loop Parameters - Read directly from Drive ==========
                
                // KP[2] = Velocity Gain [A/(cnt/sec)] -> Convert to [A/deg/sec]
                bool velocityGainLoaded = LoadVelocityGain(TIMEOUT_MS);
                if (!velocityGainLoaded)
                {
                    txtVelocityGain.Text = "0.000001";
                    AddLog("[LOAD] Warning: Using default Velocity Gain: 0.000001 A/deg/sec");
                }

                // KI[2] = Velocity Integral [Hz]
                bool velocityIntegralLoaded = LoadVelocityIntegral(TIMEOUT_MS);
                if (!velocityIntegralLoaded)
                {
                    txtVelocityIntegral.Text = "0.01";
                    AddLog("[LOAD] Warning: Using default Velocity Integral: 0.01 Hz");
                }

                // KP[3] = Position Gain [rad/sec]
                bool positionGainLoaded = LoadPositionGain(TIMEOUT_MS);
                if (!positionGainLoaded)
                {
                    txtPositionGain.Text = "1";
                    AddLog("[LOAD] Warning: Using default Position Gain: 1 rad/sec");
                }

                // FF[1] = Acc Feed Forward
                // Drive stores a raw scaled value, UI shows A/deg/sec^2
                bool accFeedForwardLoaded = LoadAccFeedForward(TIMEOUT_MS);
                if (!accFeedForwardLoaded)
                {
                    txtAccFeedForward.Text = "0";
                    AddLog("[LOAD] Warning: Using default Acc Feed Forward: 0 A/deg/sec^2");
                }

                // FF[2] = Velocity Feed Forward
                bool velFeedForwardLoaded = LoadVelFeedForward(TIMEOUT_MS);
                if (!velFeedForwardLoaded)
                {
                    txtVelFeedForward.Text = "1";
                    AddLog("[LOAD] Warning: Using default Vel Feed Forward: 1");
                }

                bool allTuningParametersLoaded =
                    velocityGainLoaded &&
                    velocityIntegralLoaded &&
                    positionGainLoaded &&
                    accFeedForwardLoaded &&
                    velFeedForwardLoaded;

                if (allTuningParametersLoaded)
                {
                    SaveTuningParametersToAppSettings(BuildCurrentTuningParametersFromUi(), "Drive load");
                }
                else
                {
                    AddLog("[LOAD] WARNING: Tuning parameters were only partially read from Drive - AppSettings not overwritten", LogType.Error);
                }

                // ========== 3. Filter Parameters ==========
                LoadFilterParameters();

                AddLog("[LOAD] SUCCESS: All parameters loaded from Drive successfully");
            }
            catch (Exception ex)
            {
                AddLog($"[LOAD] ERROR: Failed to load parameters: {ex.Message}", LogType.Error);
                LoadDefaultParameters();
            }
        }

        #region Individual Parameter Loading

        private bool LoadVelocityGain(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand("AX1.KP[2]", out response, timeout))
                {
                    AddLog($"[READ] AX1.KP[2] raw response from Drive: '{response}'", LogType.Cmd);
                    
                    if (double.TryParse(response.Trim(), out double kp2_drive))
                    {
                        double velocityGain_UI = kp2_drive * VELOCITY_GAIN_CONVERSION_FACTOR;
                        txtVelocityGain.Text = FormatToDecimal(velocityGain_UI.ToString());
                        AddLog($"[READ] SUCCESS: Velocity Gain (KP[2]) = {velocityGain_UI:F6} A/(deg/sec) (Drive value: {kp2_drive:E6} A/(cnt/sec))");
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse AX1.KP[2] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for AX1.KP[2]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading AX1.KP[2]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadVelocityIntegral(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand("AX1.KI[2]", out response, timeout))
                {
                    AddLog($"[READ] AX1.KI[2] raw response from Drive: '{response}'", LogType.Cmd);
                    
                    if (double.TryParse(response.Trim(), out double velInt))
                    {
                        txtVelocityIntegral.Text = FormatToDecimal(velInt.ToString());
                        AddLog($"[READ] SUCCESS: Velocity Integral (KI[2]) = {velInt:F6} Hz");
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse AX1.KI[2] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for AX1.KI[2]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading AX1.KI[2]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadPositionGain(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand("AX1.KP[3]", out response, timeout))
                {
                    AddLog($"[READ] AX1.KP[3] raw response from Drive: '{response}'", LogType.Cmd);
                    
                    if (double.TryParse(response.Trim(), out double posGain))
                    {
                        txtPositionGain.Text = FormatToDecimal(posGain.ToString());
                        AddLog($"[READ] SUCCESS: Position Gain (KP[3]) = {posGain:F6} rad/sec");
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse AX1.KP[3] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for AX1.KP[3]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading AX1.KP[3]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadAccFeedForward(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand("AX1.FF[1]", out response, timeout))
                {
                    AddLog($"[READ] AX1.FF[1] raw response from Drive: '{response}'", LogType.Cmd);
                    
                    if (double.TryParse(response.Trim(), out double accFFDriveRaw))
                    {
                        double accFFUi = ConvertAccFeedForwardDriveToUi(accFFDriveRaw);
                        txtAccFeedForward.Text = FormatToDecimal(accFFUi.ToString());
                        AddLog($"[READ] SUCCESS: Acc Feed Forward (FF[1]) = {accFFUi:F6} A/deg/sec^2 (Drive raw: {accFFDriveRaw:F4})");
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse AX1.FF[1] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for AX1.FF[1]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading AX1.FF[1]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private bool LoadVelFeedForward(int timeout)
        {
            try
            {
                string response;
                if (_platinumDrive.SendRawCommand("AX1.FF[2]", out response, timeout))
                {
                    AddLog($"[READ] AX1.FF[2] raw response from Drive: '{response}'", LogType.Cmd);
                    
                    if (double.TryParse(response.Trim(), out double velFF))
                    {
                        txtVelFeedForward.Text = FormatToDecimal(velFF.ToString());
                        AddLog($"[READ] SUCCESS: Vel Feed Forward (FF[2]) = {velFF:F6}");
                        return true;
                    }
                    AddLog($"[READ] ERROR: Failed to parse AX1.FF[2] response: '{response}'", LogType.Error);
                }
                else
                {
                    AddLog($"[READ] ERROR: No response from Drive for AX1.FF[2]", LogType.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"[READ] ERROR: Exception reading AX1.FF[2]: {ex.Message}", LogType.Error);
            }
            return false;
        }

        private void LoadFilterParameters()
        {
            try
            {
                AddLog("[FILTER] Reading filter parameters from Drive...");
                var filterInfo = _azimuthDrive.ReadFilterParameters();
                
                AddLog($"[FILTER] SUCCESS: Filter parameters retrieved from Drive:");
                AddLog($"[FILTER]   Filter #1: Type={filterInfo.Filter1_Type}, Freq={filterInfo.Filter1_Frequency} Hz, Damp={filterInfo.Filter1_Damping}");
                AddLog($"[FILTER]   Filter #2: Type={filterInfo.Filter2_Type}, Freq={filterInfo.Filter2_Frequency} Hz");
                AddLog($"[FILTER]   Filter #3: Type={filterInfo.Filter3_Type}, Freq={filterInfo.Filter3_Frequency} Hz");
                AddLog($"[FILTER]   Filter #4: Type={filterInfo.Filter4_Type}, Freq={filterInfo.Filter4_Frequency} Hz");
                AddLog($"[FILTER]   Feedback Filter #1: Type={filterInfo.FeedbackFilter1_Type}, Freq={filterInfo.FeedbackFilter1_Frequency} Hz");
                AddLog($"[FILTER]   Feedback Filter #2: Type={filterInfo.FeedbackFilter2_Type}, Freq={filterInfo.FeedbackFilter2_Frequency} Hz");
                
                UpdateFilterParametersUI(filterInfo);
                AddLog("[FILTER] Filter parameters updated to UI successfully");
            }
            catch (Exception filterEx)
            {
                AddLog($"[FILTER] WARNING: Could not read filter parameters: {filterEx.Message}");
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
                AddLog("[DEFAULT] Loading default parameter values...");
                
                // PD Controller Parameters
                var pdParams = AppSettings.GetAzimuthPDControllerParameters();
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
                var axisConfig = AppSettings.GetAzimuthAxisConfig();
                txtAcceleration.Text = axisConfig.Acceleration_Deg_Sec2.ToString("F2");
                txtDeceleration.Text = axisConfig.Deceleration_Deg_Sec2.ToString("F2");
                txtStopDeceleration.Text = axisConfig.Stop_Deceleration_Deg_Sec2.ToString("F2");
                
                AddLog("[DEFAULT] Default parameters loaded successfully");
            }
            catch (Exception ex)
            {
                AddLog($"[DEFAULT] ERROR: Failed to load default parameters: {ex.Message}", LogType.Error);
            }
        }
    }
}
