using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ForkMount_WPF.Engine.Camera;
using ForkMount_WPF.Engine.Utility;
using Microsoft.Win32;

namespace ForkMount_WPF.UI
{
    public partial class AutoSaveFitsControl : UserControl
    {
        private string _saveDirectory = "";
        private int _saveCount = 0;
        private int _sequenceNumber = 1;
        private CameraImageData _lastImageData;

        public bool IsAutoSaveEnabled => chkAutoSave.IsChecked == true;

        public AutoSaveFitsControl()
        {
            InitializeComponent();
            LoadSettings();
            UpdateDateStamp();
            UpdateSequenceDisplay();
            UpdateFilenamePreview();

            // Wire up change events for live preview
            chkFrameType.Checked += (s, e) => UpdateFilenamePreview();
            chkFrameType.Unchecked += (s, e) => UpdateFilenamePreview();
            chkObjectName.Checked += (s, e) => UpdateFilenamePreview();
            chkObjectName.Unchecked += (s, e) => UpdateFilenamePreview();
            chkDateStamp.Checked += (s, e) => UpdateFilenamePreview();
            chkDateStamp.Unchecked += (s, e) => UpdateFilenamePreview();
            chkSequenceNum.Checked += (s, e) => UpdateFilenamePreview();
            chkSequenceNum.Unchecked += (s, e) => UpdateFilenamePreview();
            txtFrameType.TextChanged += (s, e) => UpdateFilenamePreview();
            txtObjectName.TextChanged += (s, e) => UpdateFilenamePreview();

            // Subscribe to tracking changes immediately (singleton event, safe to subscribe early)
            Engine.Manager.TrackingManager.Instance.TrackingStateChanged += OnTrackingStateChanged;
            Engine.Manager.TrackingManager.Instance.ObjectSelectionChanged += OnObjectSelectionChanged;

            // Refresh object name once the control is fully loaded
            Loaded += (s, e) =>
            {
                RefreshObjectNameFromTracking();
                UpdateFilenamePreview();
            };

            // Also refresh when becoming visible again (e.g. tab switch back)
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible)
                {
                    RefreshObjectNameFromTracking();
                    UpdateFilenamePreview();
                }
            };
        }

        private void OnTrackingStateChanged(object sender, Engine.Manager.TrackingStateChangedEventArgs e)
        {
            Debug.WriteLine($"[AutoSaveFits] TrackingStateChanged: IsTracking={e.IsTracking}, Type={e.ObjectType}, Target={e.Target?.ObjectName ?? "(null)"}");
            Dispatcher.BeginInvoke(() =>
            {
                RefreshObjectNameFromTracking();
                UpdateFilenamePreview();
            });
        }

        private void OnObjectSelectionChanged(object sender, string objectName)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(objectName))
                {
                    txtObjectName.Text = SanitizeForFilename(objectName);
                    UpdateFilenamePreview();
                }
            });
        }

        #region Settings

        private void LoadSettings()
        {
            try
            {
                _saveDirectory = AppSettings.GetFitsAutoSaveDirectory();
                txtFrameType.Text = string.IsNullOrWhiteSpace(AppSettings.GetFitsAutoSavePrefix())
                    ? "Light"
                    : AppSettings.GetFitsAutoSavePrefix();
                UpdateDirectoryDisplay();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSaveFits] Failed to load settings: {ex.Message}");
            }
        }

        private void PersistSettings()
        {
            try
            {
                AppSettings.SaveFitsAutoSaveSettings(_saveDirectory, txtFrameType.Text.Trim());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSaveFits] Failed to save settings: {ex.Message}");
            }
        }

        #endregion

        #region UI Helpers

        private void UpdateDirectoryDisplay()
        {
            if (string.IsNullOrWhiteSpace(_saveDirectory))
            {
                txtSaveDir.Text = "(click Browse to select folder)";
                txtSaveDir.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            }
            else
            {
                txtSaveDir.Text = _saveDirectory;
                txtSaveDir.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
            }
        }

        private void UpdateDateStamp()
        {
            txtDateStamp.Text = DateTime.UtcNow.ToString("ddMMMyyyy", CultureInfo.InvariantCulture);
        }

        private void UpdateSequenceDisplay()
        {
            txtSequenceNum.Text = _sequenceNumber.ToString("D4");
        }

        private void UpdateFilenamePreview()
        {
            string name = BuildFileName(preview: true);
            if (txtFilenamePreview != null)
                txtFilenamePreview.Text = name;
        }

        /// <summary>
        /// Try to auto-fill object name from MountInfo.CurrentTrackingObjectName.
        /// This is set by TrackingManager whenever tracking starts/stops.
        /// </summary>
        private void RefreshObjectNameFromTracking()
        {
            try
            {
                // Primary: read directly from TrackingManager (works always, even without PollingManager)
                var target = Engine.Manager.TrackingManager.Instance.CurrentTarget;
                if (target != null && !string.IsNullOrWhiteSpace(target.ObjectName))
                {
                    Debug.WriteLine($"[AutoSaveFits] RefreshObjectName: TrackingManager.CurrentTarget.ObjectName = '{target.ObjectName}'");
                    txtObjectName.Text = SanitizeForFilename(target.ObjectName);
                    return;
                }

                // Fallback: try MountInfo (may be null if PollingManager not initialized)
                var mountInfo = App.MountInfo;
                if (mountInfo != null)
                {
                    string objName = mountInfo.CurrentTrackingObjectName;
                    Debug.WriteLine($"[AutoSaveFits] RefreshObjectName: MountInfo.CurrentTrackingObjectName = '{objName}'");
                    if (!string.IsNullOrWhiteSpace(objName))
                    {
                        txtObjectName.Text = SanitizeForFilename(objName);
                        return;
                    }
                }

                Debug.WriteLine("[AutoSaveFits] RefreshObjectName: No tracking object found");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSaveFits] RefreshObjectName error: {ex.Message}");
            }
        }

        #endregion

        #region Filename Builder

        private string BuildFileName(bool preview = false)
        {
            var parts = new StringBuilder();

            if (chkFrameType.IsChecked == true)
            {
                string ft = txtFrameType.Text.Trim();
                if (!string.IsNullOrEmpty(ft))
                    parts.Append(SanitizeForFilename(ft));
            }

            if (chkObjectName.IsChecked == true)
            {
                string obj = txtObjectName.Text.Trim();
                if (!string.IsNullOrEmpty(obj))
                {
                    if (parts.Length > 0) parts.Append('_');
                    parts.Append(SanitizeForFilename(obj));
                }
            }

            if (chkDateStamp.IsChecked == true)
            {
                string ds = preview
                    ? DateTime.UtcNow.ToString("ddMMMyyyy", CultureInfo.InvariantCulture)
                    : txtDateStamp.Text.Trim();
                if (!string.IsNullOrEmpty(ds))
                {
                    if (parts.Length > 0) parts.Append('_');
                    parts.Append(ds);
                }
            }

            if (chkSequenceNum.IsChecked == true)
            {
                if (parts.Length > 0) parts.Append('_');
                parts.Append(_sequenceNumber.ToString("D4"));
            }

            if (parts.Length == 0)
                parts.Append("Image");

            parts.Append(".fits");
            return parts.ToString();
        }

        private static string SanitizeForFilename(string input)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns the target save directory.
        /// If Object Name is enabled and non-empty, creates a subfolder under _saveDirectory.
        /// e.g. D:\Astro\Captures\Rigel\
        /// </summary>
        private string GetSaveDirectory()
        {
            string dir = _saveDirectory;

            if (chkObjectName.IsChecked == true)
            {
                string obj = txtObjectName.Text.Trim();
                if (!string.IsNullOrEmpty(obj))
                {
                    dir = Path.Combine(dir, SanitizeForFilename(obj));
                }
            }

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return dir;
        }

        #endregion

        #region Events

        private void ChkAutoSave_Changed(object sender, RoutedEventArgs e)
        {
            if (chkAutoSave.IsChecked == true && string.IsNullOrWhiteSpace(_saveDirectory))
            {
                BtnBrowse_Click(sender, e);
                if (string.IsNullOrWhiteSpace(_saveDirectory))
                {
                    chkAutoSave.IsChecked = false;
                    return;
                }
            }

            if (chkAutoSave.IsChecked == true)
            {
                // Reset sequence and date when enabling
                _sequenceNumber = 1;
                _saveCount = 0;
                txtSaveCount.Text = "0 saved";
                UpdateDateStamp();
                UpdateSequenceDisplay();
                RefreshObjectNameFromTracking();
                UpdateFilenamePreview();
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Select folder to save FITS files"
                };

                if (!string.IsNullOrWhiteSpace(_saveDirectory) && Directory.Exists(_saveDirectory))
                    dialog.InitialDirectory = _saveDirectory;

                if (dialog.ShowDialog() == true)
                {
                    _saveDirectory = dialog.FolderName;
                    UpdateDirectoryDisplay();
                    PersistSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSaveFits] Browse error: {ex.Message}");
            }
        }

        #endregion

        #region Save Logic

        /// <summary>
        /// Called by ImageVerificationAndOffset after each successful capture.
        /// </summary>
        public async Task OnImageCaptured(CameraImageData imageData)
        {
            // Always cache the latest image so manual Save works even if auto-save is off
            if (imageData != null)
                _lastImageData = imageData;

            if (!IsAutoSaveEnabled || imageData == null)
                return;

            if (string.IsNullOrWhiteSpace(_saveDirectory))
                return;

            // Check frame limit
            if (int.TryParse(txtTotalFrames.Text, out int maxFrames) && maxFrames > 0)
            {
                if (_saveCount >= maxFrames)
                {
                    txtLastSaved.Text = $"Done ({maxFrames} frames)";
                    txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                    return;
                }
            }

            try
            {
                // Update dynamic parts (date/sequence only - do NOT overwrite user-edited object name)
                UpdateDateStamp();
                UpdateSequenceDisplay();
                UpdateFilenamePreview();

                string targetDir = GetSaveDirectory();
                string fileName = BuildFileName();
                string fullPath = Path.Combine(targetDir, fileName);

                bool success = await Task.Run(() => imageData.SaveToFITS(fullPath));

                if (success)
                {
                    _saveCount++;
                    _sequenceNumber++;
                    txtSaveCount.Text = $"{_saveCount} saved";
                    UpdateSequenceDisplay();
                    UpdateFilenamePreview();

                    txtLastSaved.Text = fileName;
                    txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
                    Debug.WriteLine($"[AutoSaveFits] Saved: {fullPath}");

                    PersistSettings();

                    // Check if we just hit the limit
                    if (maxFrames > 0 && _saveCount >= maxFrames)
                    {
                        txtLastSaved.Text = $"Done ({maxFrames}/{maxFrames})";
                        txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                    }
                }
                else
                {
                    txtLastSaved.Text = "Save failed";
                    txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSaveFits] Error: {ex.Message}");
                txtLastSaved.Text = ex.Message;
                txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }

        private async void BtnManualSave_Click(object sender, RoutedEventArgs e)
        {
            if (_lastImageData == null)
            {
                txtLastSaved.Text = "No image to save";
                txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                return;
            }

            if (string.IsNullOrWhiteSpace(_saveDirectory))
            {
                BtnBrowse_Click(sender, e);
                if (string.IsNullOrWhiteSpace(_saveDirectory))
                    return;
            }

            try
            {
                // Update dynamic parts (date/sequence only - do NOT overwrite user-edited object name)
                UpdateDateStamp();
                UpdateSequenceDisplay();
                UpdateFilenamePreview();

                string targetDir = GetSaveDirectory();
                string fileName = BuildFileName();
                string fullPath = Path.Combine(targetDir, fileName);

                btnManualSave.IsEnabled = false;
                bool success = await Task.Run(() => _lastImageData.SaveToFITS(fullPath));
                btnManualSave.IsEnabled = true;

                if (success)
                {
                    _saveCount++;
                    _sequenceNumber++;
                    txtSaveCount.Text = $"{_saveCount} saved";
                    UpdateSequenceDisplay();
                    UpdateFilenamePreview();

                    txtLastSaved.Text = fileName;
                    txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
                    Debug.WriteLine($"[AutoSaveFits] Manual save: {fullPath}");
                }
                else
                {
                    txtLastSaved.Text = "Save failed";
                    txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            }
            catch (Exception ex)
            {
                btnManualSave.IsEnabled = true;
                Debug.WriteLine($"[AutoSaveFits] Manual save error: {ex.Message}");
                txtLastSaved.Text = ex.Message;
                txtLastSaved.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }

        #endregion
    }
}
