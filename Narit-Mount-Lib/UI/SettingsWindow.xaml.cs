using System;
using System.Windows;
using System.Windows.Input;
using ForkMount_WPF.Engine;

namespace ForkMount_WPF.UI
{
    public partial class SettingsWindow : Window
    {
        private bool _changesMade = false;

        public SettingsWindow()
        {
            InitializeComponent();
            
            // Initially show buttons for Location/Time tab (default selected)
            UpdateButtonsVisibility();
        }

        private void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateButtonsVisibility();
        }

        private void UpdateButtonsVisibility()
        {
            // Show buttons only when Location/Time tab is selected (index 0)
            // Hide buttons when Mount Calibration tab is selected (index 1)
            if (mainTabControl != null && bottomButtonsPanel != null)
            {
                bool isLocationTimeTab = mainTabControl.SelectedIndex == 0;
                bottomButtonsPanel.Visibility = isLocationTimeTab ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save LocationTimeControl settings
                if (!locationTimeControl.SaveSettings())
                {
                    txtStatus.Text = "Please fix validation errors in Location/Time tab";
                    txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }

                // Save MountCalibrationControl settings
                if (!mountCalibrationControl.SaveSettings())
                {
                    txtStatus.Text = "Please fix validation errors in Mount Calibration tab";
                    txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }

                txtStatus.Text = "Settings saved successfully!";
                txtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                
                _changesMade = true;

                // Close after short delay
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    Close();  // Just close, no DialogResult needed for non-modal window
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error saving settings: {ex.Message}";
                txtStatus.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();  // Just close, no DialogResult needed for non-modal window
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();  // Just close, no DialogResult needed for non-modal window
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            UpdateMaximizeButtonGlyph();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2 && ResizeMode != ResizeMode.NoResize)
                {
                    MaximizeButton_Click(sender, new RoutedEventArgs());
                    return;
                }

                DragMove();
            }
        }

        public bool ChangesMade => _changesMade;

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            UpdateMaximizeButtonGlyph();
        }

        private void UpdateMaximizeButtonGlyph()
        {
            if (btnMaximize != null)
            {
                btnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
            }
        }
    }
}
