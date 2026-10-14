using System;
using System.Windows;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Altitude_Control_Parameters_Window - Save to Flash Memory
    /// Saves Elmo internal loop parameters to Drive Flash (Axis 2)
    /// </summary>
    public partial class Altitude_Control_Parameters_Window
    {
        #region Save to Flash

        private void BtnSaveFlash_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
                {
                    AddLog("[SAVE] ERROR: Drive not connected - Cannot save to flash", LogType.Error);
                    return;
                }

                AddLog("[SAVE] Saving parameters to Drive Flash (Axis 2)...", LogType.Message);
                btnSaveFlash.IsEnabled = false;

                // Make sure the latest UI values are pushed to Drive RAM before persisting.
                SaveMotionParameters();
                ApplyTuningParameters();

                // Queue SAVE command for Axis 2
                _pollingManager.EnqueueExtraCommand($"{_axisPrefix}.SAVE", async () =>
                {
                    try
                    {
                        string response;
                        bool success = _platinumDrive.SendRawCommand($"{_axisPrefix}.SAVE", out response);
                        
                        if (success)
                        {
                            AddLog("[SAVE] Parameters saved to Flash successfully (Axis 2)", LogType.Message);
                            
                            // Re-enable button
                            _ = Dispatcher.InvokeAsync(() =>
                            {
                                btnSaveFlash.IsEnabled = true;
                            });
                        }
                        else
                        {
                            AddLog($"[SAVE] ERROR: Failed to save to flash: {response}", LogType.Error);
                            
                            // Re-enable button
                            _ = Dispatcher.InvokeAsync(() =>
                            {
                                btnSaveFlash.IsEnabled = true;
                            });
                        }
                        
                        return success;
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[SAVE] ERROR: Exception during save: {ex.Message}", LogType.Error);
                        
                        // Re-enable button
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            btnSaveFlash.IsEnabled = true;
                        });
                        
                        return false;
                    }
                });

                AddLog($"[SAVE] SAVE command queued (Queue size: {_pollingManager.ExtraCommandQueueSize})", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[SAVE] ERROR: Failed to queue save command: {ex.Message}", LogType.Error);
                btnSaveFlash.IsEnabled = true;
            }
        }

        #endregion
    }
}
