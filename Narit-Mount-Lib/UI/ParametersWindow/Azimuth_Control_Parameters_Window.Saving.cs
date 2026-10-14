using System;
using System.Windows;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Azimuth_Control_Parameters_Window - Saving Parameters
    /// Handles saving parameters to Drive RAM and Flash memory
    /// </summary>
    public partial class Azimuth_Control_Parameters_Window
    {
        /// <summary>
        /// Save Parameters and Offsets
        /// This sends ALL parameters to Drive and saves to Flash memory
        /// </summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save Offsets
                SaveOffsets();

                // Save Tuning Parameters to Drive (if connected)
                if (_platinumDrive != null && _platinumDrive.IsConnected)
                {
                    try
                    {
                        AddLog("[SAVE] Starting save process - All parameters will be sent to Drive RAM then Flash");
                        
                        // ========== 1. Send all parameters to Drive RAM ==========
                        SaveAllParametersToRAM();
                        
                        AddLog("[SAVE] SUCCESS: All parameters sent to Drive RAM");
                        
                        // ========== 2. Save to Flash Memory ==========
                        SaveToFlash();
                    }
                    catch (Exception driveEx)
                    {
                        AddLog($"[SAVE] ERROR: Failed to save to Drive: {driveEx.Message}", LogType.Error);
                    }
                }
                else
                {
                    AddLog("[SAVE] WARNING: Drive not connected - Cannot save parameters to Drive", LogType.Error);
                }

                AddLog($"[SAVE] Save process completed");
            }
            catch (Exception ex)
            {
                AddLog($"[SAVE] ERROR: Save process failed: {ex.Message}", LogType.Error);
            }
        }

        /// <summary>
        /// Save all tuning parameters to Drive RAM
        /// </summary>
        private void SaveAllParametersToRAM()
        {
            string response;
            
            // KP[2] = Velocity Loop Gain - Convert UI units [A/(deg/sec)] -> Drive units [A/(cnt/sec)]
            double velocityGain_UI = ParseDouble(txtVelocityGain.Text, 0);
            double kp2_drive = velocityGain_UI / VELOCITY_GAIN_CONVERSION_FACTOR; 
            string kp2Command = $"AX1.KP[2]={kp2_drive:E10}";
            if (_platinumDrive.SendRawCommand(kp2Command, out response))
            {
                AddLog($"[WRITE] AX1.KP[2] = {kp2_drive:E6} A/(cnt/sec) sent to Drive RAM (UI: {velocityGain_UI:F6} A/(deg/sec))");
            }
            else
            {
                AddLog($"[WRITE] ERROR: Failed to write AX1.KP[2] to Drive", LogType.Error);
            }
            
            // KI[2] = Velocity Integral [Hz]
            double velocityIntegral = ParseDouble(txtVelocityIntegral.Text, 0);
            string kiCommand = $"AX1.KI[2]={velocityIntegral:E10}";
            if (_platinumDrive.SendRawCommand(kiCommand, out response))
            {
                AddLog($"[WRITE] AX1.KI[2] = {velocityIntegral:F6} Hz sent to Drive RAM");
            }
            else
            {
                AddLog($"[WRITE] ERROR: Failed to write AX1.KI[2] to Drive", LogType.Error);
            }
            
            // KP[3] = Position Gain [rad/sec]
            double positionGain = ParseDouble(txtPositionGain.Text, 0);
            string kp3Command = $"AX1.KP[3]={positionGain:E10}";
            if (_platinumDrive.SendRawCommand(kp3Command, out response))
            {
                AddLog($"[WRITE] AX1.KP[3] = {positionGain:F6} rad/sec sent to Drive RAM");
            }
            else
            {
                AddLog($"[WRITE] ERROR: Failed to write AX1.KP[3] to Drive", LogType.Error);
            }
            
            // FF[1] = Acc Feed Forward
            // UI uses A/deg/sec^2, Drive expects the raw scaled FF[1] value.
            double accFeedForwardUi = ParseDouble(txtAccFeedForward.Text, 0);
            double accFeedForwardDrive = ConvertAccFeedForwardUiToDrive(accFeedForwardUi);
            string ff1Command = $"AX1.FF[1]={accFeedForwardDrive:E10}";
            if (_platinumDrive.SendRawCommand(ff1Command, out response))
            {
                AddLog($"[WRITE] AX1.FF[1] = {accFeedForwardUi:F6} A/deg/sec^2 sent to Drive RAM (raw: {accFeedForwardDrive:F4})");
            }
            else
            {
                AddLog($"[WRITE] ERROR: Failed to write AX1.FF[1] to Drive", LogType.Error);
            }
            
            // FF[2] = Vel Feed Forward
            double velFeedForward = ParseDouble(txtVelFeedForward.Text, 0);
            string ff2Command = $"AX1.FF[2]={velFeedForward:E10}";
            if (_platinumDrive.SendRawCommand(ff2Command, out response))
            {
                AddLog($"[WRITE] AX1.FF[2] = {velFeedForward:F6} sent to Drive RAM");
            }
            else
            {
                AddLog($"[WRITE] ERROR: Failed to write AX1.FF[2] to Drive", LogType.Error);
            }
        }

        /// <summary>
        /// Save to Flash Memory
        /// </summary>
        private void SaveToFlash()
        {
            string response;
            bool wasEnabled = false;
            
            try
            {
                // Check if drive is enabled
                if (_platinumDrive.SendRawCommand("AX1.MO", out response))
                {
                    wasEnabled = (response?.Trim() == "1");
                    AddLog($"[FLASH] Drive status check: {(wasEnabled ? "Enabled (MO=1)" : "Disabled (MO=0)")}");
                }
                
                // If enabled, disable it (via queue)
                if (wasEnabled)
                {
                    AddLog("[FLASH] Disabling drive temporarily (required for Flash save)...");
                    
                    _pollingManager?.EnqueueExtraCommand("AX1.MO=0", async () =>
                    {
                        string resp;
                        bool success = _platinumDrive.SendRawCommand("AX1.MO=0", out resp);
                        if (success)
                        {
                            AddLog("[FLASH] Drive disabled successfully");
                        }
                        else
                        {
                            AddLog("[FLASH] ERROR: Failed to disable drive", LogType.Error);
                        }
                        return success;
                    });
                }
                
                AddLog("[FLASH] Executing Flash save command (SV)...");
                AddLog("[FLASH] NOTE: Commands queued - will execute in 1-2 polling cycles (~30-45ms)");
                
                // Queue SV command (via queue)
                _pollingManager?.EnqueueExtraCommand("SV", async () =>
                {
                    string resp;
                    bool success = _platinumDrive.SendRawCommand("SV", out resp, 10000);
                    if (success)
                    {
                        AddLog("[FLASH] SUCCESS: Parameters saved to non-volatile memory");
                        AddLog("[FLASH] All settings will persist after power cycle");
                    }
                    else
                    {
                        AddLog("[FLASH] ERROR: Failed to save to Flash memory", LogType.Error);
                        AddLog("[FLASH] WARNING: Parameters are in RAM only (will be lost on power cycle)", LogType.Error);
                        AddLog("[FLASH] SUGGESTION: Use EAS II software to save parameters permanently", LogType.Message);
                    }
                    return success;
                });
                
                // Re-enable drive if it was enabled before (via queue)
                if (wasEnabled)
                {
                    AddLog("[FLASH] Re-enabling drive...");
                    
                    _pollingManager?.EnqueueExtraCommand("AX1.MO=1", async () =>
                    {
                        string resp;
                        bool success = _platinumDrive.SendRawCommand("AX1.MO=1", out resp);
                        if (success)
                        {
                            AddLog("[FLASH] Drive re-enabled successfully (MO=1)");
                        }
                        else
                        {
                            AddLog("[FLASH] WARNING: Failed to re-enable drive", LogType.Error);
                        }

                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            btnSaveFlash.IsEnabled = true;
                        });

                        return success;
                    });
                }
                else
                {
                    _pollingManager?.EnqueueExtraCommand("FLASH_UI_COMPLETE", async () =>
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            btnSaveFlash.IsEnabled = true;
                        });
                        return true;
                    });
                }
            }
            catch (Exception flashEx)
            {
                AddLog($"[FLASH] ERROR: Flash save exception: {flashEx.Message}", LogType.Error);
                AddLog("[FLASH] WARNING: Parameters are in RAM only", LogType.Error);
                
                // Try to re-enable if it was enabled (emergency fallback - direct send)
                if (wasEnabled)
                {
                    try
                    {
                        _platinumDrive.SendRawCommand("AX1.MO=1", out response);
                        AddLog("[FLASH] Drive re-enabled (emergency fallback)");
                    }
                    catch { }
                }

                _ = Dispatcher.InvokeAsync(() =>
                {
                    btnSaveFlash.IsEnabled = true;
                });
            }
        }

        /// <summary>
        /// Save to Flash button click handler
        /// Queues the current UI values into Drive RAM first, then persists them
        /// using the shared SV flash-save sequence.
        /// </summary>
        private void BtnSaveFlash_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
                {
                    AddLog("[SAVE] ERROR: Drive not connected - Cannot save to flash", LogType.Error);
                    return;
                }

                AddLog("[SAVE] Saving parameters to Drive Flash (Axis 1)...", LogType.Message);
                btnSaveFlash.IsEnabled = false;

                SaveOffsets();
                SaveMotionParameters();
                ApplyTuningParameters();
                SaveToFlash();

                AddLog($"[SAVE] Flash persistence sequence queued (Queue size: {_pollingManager.ExtraCommandQueueSize})", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[SAVE] ERROR: Failed to queue save command: {ex.Message}", LogType.Error);
                btnSaveFlash.IsEnabled = true;
            }
        }

        /// <summary>
        /// Save Offsets to AppSettings
        /// </summary>
        private void SaveOffsets()
        {
            var offsets = new TuningOffsets
            {
                Kp_Offset = ParseDouble(txtKpOffset.Text, 0),
                Ki_Offset = ParseDouble(txtKiOffset.Text, 0),
                Kd_Offset = ParseDouble(txtKdOffset.Text, 0),
                Deadband_Offset = ParseDouble(txtDeadbandOffset.Text, 0),
                Velocity_Gain_Offset = ParseDouble(txtVelocityGainOffset.Text, 0),
                Velocity_Integral_Offset = ParseDouble(txtVelocityIntegralOffset.Text, 0),
                Position_Gain_Offset = ParseDouble(txtPositionGainOffset.Text, 0),
                Acc_Feed_Forward_Offset = ParseDouble(txtAccFeedForwardOffset.Text, 0),
                Vel_Feed_Forward_Offset = ParseDouble(txtVelFeedForwardOffset.Text, 0)
            };
            
            AppSettings.SaveTuningOffsets(offsets);
            AddLog("[OFFSET] Tuning offsets saved to config file");
        }
    }
}
