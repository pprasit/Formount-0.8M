using System;
using System.Windows;
using System.Windows.Controls;
using ForkMount_WPF.Engine.Info;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.UI
{
    /// <summary>
    /// Altitude_Control_Parameters_Window - Filter Parameters
    /// Handles Filter #1-3 for Altitude (Axis 2)
    /// </summary>
    public partial class Altitude_Control_Parameters_Window
    {
        #region Filter #1 Events

        private void CmbFilter1Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            int filterType = GetFilterTypeValue(cmbFilter1Type);
            ApplyFilterParameter($"{_axisPrefix}.KV[5]", filterType, "Filter #1 Type");
        }

        private void TxtFilter1Freq_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter1Freq.Text = FormatToDecimal(txtFilter1Freq.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[1]", ParseDouble(txtFilter1Freq.Text, 800), "Filter #1 Frequency");
        }

        private void TxtFilter1Damp_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter1Damp.Text = FormatToDecimal(txtFilter1Damp.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[2]", ParseDouble(txtFilter1Damp.Text, 0.5), "Filter #1 Damping");
        }

        #endregion

        #region Filter #2 Events

        private void CmbFilter2Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            int filterType = GetFilterTypeValue(cmbFilter2Type);
            ApplyFilterParameter($"{_axisPrefix}.KV[10]", filterType, "Filter #2 Type");
        }

        private void TxtFilter2Freq_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter2Freq.Text = FormatToDecimal(txtFilter2Freq.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[6]", ParseDouble(txtFilter2Freq.Text, 40), "Filter #2 Frequency");
        }

        private void TxtFilter2Q_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter2Q.Text = FormatToDecimal(txtFilter2Q.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[7]", ParseDouble(txtFilter2Q.Text, 0.7), "Filter #2 Q Factor");
        }

        private void TxtFilter2Mag_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter2Mag.Text = FormatToDecimal(txtFilter2Mag.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[8]", ParseDouble(txtFilter2Mag.Text, 0), "Filter #2 Magnitude");
        }

        #endregion

        #region Filter #3 Events

        private void CmbFilter3Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            int filterType = GetFilterTypeValue(cmbFilter3Type);
            ApplyFilterParameter($"{_axisPrefix}.KV[15]", filterType, "Filter #3 Type");
        }

        private void TxtFilter3Freq_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter3Freq.Text = FormatToDecimal(txtFilter3Freq.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[11]", ParseDouble(txtFilter3Freq.Text, 0), "Filter #3 Frequency");
        }

        private void TxtFilter3Q_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter3Q.Text = FormatToDecimal(txtFilter3Q.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[12]", ParseDouble(txtFilter3Q.Text, 5), "Filter #3 Q Factor");
        }

        private void TxtFilter3Mag_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter3Mag.Text = FormatToDecimal(txtFilter3Mag.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[13]", ParseDouble(txtFilter3Mag.Text, 0), "Filter #3 Magnitude");
        }

        #endregion

        #region Filter #4 Events

        private void CmbFilter4Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            int filterType = GetFilterTypeValue(cmbFilter4Type);
            ApplyFilterParameter($"{_axisPrefix}.KV[20]", filterType, "Filter #4 Type");
        }

        private void TxtFilter4Freq_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter4Freq.Text = FormatToDecimal(txtFilter4Freq.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[16]", ParseDouble(txtFilter4Freq.Text, 60), "Filter #4 Frequency");
        }

        private void TxtFilter4Q_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter4Q.Text = FormatToDecimal(txtFilter4Q.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[17]", ParseDouble(txtFilter4Q.Text, 6), "Filter #4 Q Factor");
        }

        private void TxtFilter4Mag_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFilter4Mag.Text = FormatToDecimal(txtFilter4Mag.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[18]", ParseDouble(txtFilter4Mag.Text, 10), "Filter #4 Magnitude");
        }

        #endregion

        #region Feedback Filter #1 Events

        private void CmbFeedbackFilter1Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            int filterType = GetFilterTypeValue(cmbFeedbackFilter1Type);
            ApplyFilterParameter($"{_axisPrefix}.KV[55]", filterType, "Feedback Filter #1 Type");
        }

        private void TxtFeedbackFilter1Freq_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFeedbackFilter1Freq.Text = FormatToDecimal(txtFeedbackFilter1Freq.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[51]", ParseDouble(txtFeedbackFilter1Freq.Text, 0), "Feedback Filter #1 Frequency");
        }

        private void TxtFeedbackFilter1Q_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFeedbackFilter1Q.Text = FormatToDecimal(txtFeedbackFilter1Q.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[52]", ParseDouble(txtFeedbackFilter1Q.Text, 0), "Feedback Filter #1 Q Factor");
        }

        private void TxtFeedbackFilter1Mag_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFeedbackFilter1Mag.Text = FormatToDecimal(txtFeedbackFilter1Mag.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[53]", ParseDouble(txtFeedbackFilter1Mag.Text, 0), "Feedback Filter #1 Magnitude");
        }

        #endregion

        #region Feedback Filter #2 Events

        private void CmbFeedbackFilter2Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            int filterType = GetFilterTypeValue(cmbFeedbackFilter2Type);
            ApplyFilterParameter($"{_axisPrefix}.KV[60]", filterType, "Feedback Filter #2 Type");
        }

        private void TxtFeedbackFilter2Freq_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFeedbackFilter2Freq.Text = FormatToDecimal(txtFeedbackFilter2Freq.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[56]", ParseDouble(txtFeedbackFilter2Freq.Text, 0), "Feedback Filter #2 Frequency");
        }

        private void TxtFeedbackFilter2Q_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFeedbackFilter2Q.Text = FormatToDecimal(txtFeedbackFilter2Q.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[57]", ParseDouble(txtFeedbackFilter2Q.Text, 0), "Feedback Filter #2 Q Factor");
        }

        private void TxtFeedbackFilter2Mag_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            txtFeedbackFilter2Mag.Text = FormatToDecimal(txtFeedbackFilter2Mag.Text);
            ApplyFilterParameter($"{_axisPrefix}.KV[58]", ParseDouble(txtFeedbackFilter2Mag.Text, 0), "Feedback Filter #2 Magnitude");
        }

        #endregion

        #region Apply Filter Parameter

        /// <summary>
        /// Apply single filter parameter to Drive (Axis 2 - RAM only)
        /// </summary>
        private void ApplyFilterParameter(string kvCommand, double value, string paramName)
        {
            if (_pollingManager == null || _platinumDrive == null || !_platinumDrive.IsConnected)
            {
                AddLog($"[FILTER] WARNING: {paramName} updated in UI only - Drive not connected", LogType.Warning);
                return;
            }

            try
            {
                AddLog($"[FILTER] Queuing: {kvCommand}={value:F6} ({paramName})", LogType.Message);
                
                _pollingManager.EnqueueExtraCommand(kvCommand, async () =>
                {
                    try
                    {
                        string fullCommand = $"{kvCommand}={value:F10}";
                        string response;

                        bool success = _platinumDrive.SendRawCommand(fullCommand, out response);
                        
                        if (success)
                        {
                            AddLog($"[FILTER] SUCCESS: {paramName} set to {value:F2} (RAM)", LogType.Message);
                        }
                        else
                        {
                            AddLog($"[FILTER] ERROR: Failed to set {paramName} - Response: {response}", LogType.Error);
                        }
                        
                        return success;
                    }
                    catch (Exception ex)
                    {
                        AddLog($"[FILTER] ERROR: Exception setting {paramName}: {ex.Message}", LogType.Error);
                        return false;
                    }
                });
                
                AddLog($"[FILTER] Queued successfully (Queue size: {_pollingManager.ExtraCommandQueueSize})", LogType.Message);
            }
            catch (Exception ex)
            {
                AddLog($"[FILTER] ERROR: Failed to queue {paramName}: {ex.Message}", LogType.Error);
            }
        }

        #endregion

        #region Filter Type Helper Methods

        /// <summary>
        /// Get Filter Type Value from ComboBox (returns integer 0-7)
        /// </summary>
        private int GetFilterTypeValue(ComboBox comboBox)
        {
            if (comboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null)
            {
                return int.Parse(selectedItem.Tag.ToString() ?? "0");
            }
            return 0; // Default to Disable
        }

        /// <summary>
        /// Set Filter Type ComboBox by Value (0-7)
        /// 0 = Disable, 1 = Low-Pass, 2 = Lead-Lag, 3 = Double Lead-Lag,
        /// 4 = Notch, 5 = Anti-Notch, 6 = General-Biquad, 7 = Pole Zero
        /// </summary>
        private void SetFilterTypeValue(ComboBox comboBox, int value)
        {
            // Clamp value to valid range (0-7)
            value = Math.Clamp(value, 0, 7);
            
            // Set SelectedIndex (value because index 0 = Disable with Tag 0)
            comboBox.SelectedIndex = value;
        }

        /// <summary>
        /// Update Filter Parameters UI from AltitudeInfo
        /// </summary>
        private void UpdateFilterParametersUI(AltitudeInfo info)
        {
            if (info == null) return;

            // Filter #1
            SetFilterTypeValue(cmbFilter1Type, info.Filter1_Type);
            txtFilter1Freq.Text = FormatToDecimal(info.Filter1_Frequency.ToString());
            txtFilter1Damp.Text = FormatToDecimal(info.Filter1_Damping.ToString());

            // Filter #2
            SetFilterTypeValue(cmbFilter2Type, info.Filter2_Type);
            txtFilter2Freq.Text = FormatToDecimal(info.Filter2_Frequency.ToString());
            txtFilter2Q.Text = FormatToDecimal(info.Filter2_QFactor.ToString());
            txtFilter2Mag.Text = FormatToDecimal(info.Filter2_Magnitude.ToString());

            // Filter #3
            SetFilterTypeValue(cmbFilter3Type, info.Filter3_Type);
            txtFilter3Freq.Text = FormatToDecimal(info.Filter3_Frequency.ToString());
            txtFilter3Q.Text = FormatToDecimal(info.Filter3_QFactor.ToString());
            txtFilter3Mag.Text = FormatToDecimal(info.Filter3_Magnitude.ToString());

            // Filter #4
            SetFilterTypeValue(cmbFilter4Type, info.Filter4_Type);
            txtFilter4Freq.Text = FormatToDecimal(info.Filter4_Frequency.ToString());
            txtFilter4Q.Text = FormatToDecimal(info.Filter4_QFactor.ToString());
            txtFilter4Mag.Text = FormatToDecimal(info.Filter4_Magnitude.ToString());

            // Feedback Filter #1
            SetFilterTypeValue(cmbFeedbackFilter1Type, info.FeedbackFilter1_Type);
            txtFeedbackFilter1Freq.Text = FormatToDecimal(info.FeedbackFilter1_Frequency.ToString());
            txtFeedbackFilter1Q.Text = FormatToDecimal(info.FeedbackFilter1_QFactor.ToString());
            txtFeedbackFilter1Mag.Text = FormatToDecimal(info.FeedbackFilter1_Magnitude.ToString());

            // Feedback Filter #2
            SetFilterTypeValue(cmbFeedbackFilter2Type, info.FeedbackFilter2_Type);
            txtFeedbackFilter2Freq.Text = FormatToDecimal(info.FeedbackFilter2_Frequency.ToString());
            txtFeedbackFilter2Q.Text = FormatToDecimal(info.FeedbackFilter2_QFactor.ToString());
            txtFeedbackFilter2Mag.Text = FormatToDecimal(info.FeedbackFilter2_Magnitude.ToString());
        }

        #endregion
    }
}
