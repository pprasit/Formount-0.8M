using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// Information about a captured image for thumbnail gallery
    /// Includes ALL metadata for proper restoration and analysis
    /// </summary>
    public class CapturedImageThumbnail : INotifyPropertyChanged
    {
        private bool _isSelected;
        private BitmapSource _thumbnail;

        /// <summary>
        /// Unique ID for this capture
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Display thumbnail (downscaled for performance)
        /// </summary>
        public BitmapSource Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail != value)
                {
                    _thumbnail = value;
                    OnPropertyChanged(nameof(Thumbnail));
                }
            }
        }

        /// <summary>
        /// Full-resolution image (stretched for display)
        /// </summary>
        public BitmapSource FullImage { get; set; }

        /// <summary>
        /// Raw camera image data (for re-processing)
        /// </summary>
        public CameraImageData RawData { get; set; }

        /// <summary>
        /// Capture timestamp
        /// </summary>
        public DateTime CaptureTime { get; set; }

        // ========== SERIES METADATA ==========

        /// <summary>
        /// Series ID - images from the same series share the same SeriesId
        /// </summary>
        public Guid? SeriesId { get; set; }

        /// <summary>
        /// Index within the series (1, 2, 3, ...)
        /// </summary>
        public int? SeriesIndex { get; set; }

        /// <summary>
        /// Is this image part of a series?
        /// </summary>
        public bool IsPartOfSeries => SeriesId.HasValue;

        // ========== HISTOGRAM STRETCH METADATA ==========
        
        /// <summary>
        /// Histogram stretch min value when this image was captured
        /// </summary>
        public int StretchMin { get; set; }

        /// <summary>
        /// Histogram stretch max value when this image was captured
        /// </summary>
        public int StretchMax { get; set; }

        /// <summary>
        /// Stretch profile type used for this image
        /// </summary>
        public string StretchProfile { get; set; }

        // ========== SERIES COLOR ==========

        // Predefined palette of visually distinct colors for series grouping
        private static readonly Color[] SeriesColors = new[]
        {
            Color.FromRgb(0xF5, 0x9E, 0x0B), // Amber
            Color.FromRgb(0x8B, 0x5C, 0xF6), // Violet
            Color.FromRgb(0xEF, 0x44, 0x44), // Red
            Color.FromRgb(0x3B, 0x82, 0xF6), // Blue
            Color.FromRgb(0x10, 0xB9, 0x81), // Emerald
            Color.FromRgb(0xEC, 0x48, 0x99), // Pink
            Color.FromRgb(0x06, 0xB6, 0xD4), // Cyan
            Color.FromRgb(0xF9, 0x73, 0x16), // Orange
        };

        // Cache brushes per SeriesId so every thumbnail in the same set shares the same frozen brush
        private static readonly Dictionary<Guid, SolidColorBrush> _seriesBrushCache = new Dictionary<Guid, SolidColorBrush>();
        private static int _nextColorIndex;

        // Default border brush for non-series thumbnails
        private static readonly SolidColorBrush DefaultBrush = CreateFrozenBrush(0x3F, 0x3F, 0x46);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Border brush for this thumbnail – returns the series color if part of a series,
        /// or the default gray border otherwise. Bind directly (not inside a Style trigger).
        /// </summary>
        public SolidColorBrush DisplayBorderBrush
        {
            get
            {
                if (!SeriesId.HasValue)
                    return DefaultBrush;

                if (!_seriesBrushCache.TryGetValue(SeriesId.Value, out var brush))
                {
                    var color = SeriesColors[_nextColorIndex % SeriesColors.Length];
                    _nextColorIndex++;
                    brush = new SolidColorBrush(color);
                    brush.Freeze();
                    _seriesBrushCache[SeriesId.Value] = brush;
                }

                return brush;
            }
        }

        // ========== DISPLAY PROPERTIES ==========

        /// <summary>
        /// Image dimensions
        /// </summary>
        public string ImageSize => RawData != null ? $"{RawData.Width}x{RawData.Height}" : "--";

        /// <summary>
        /// Exposure settings
        /// </summary>
        public string ExposureInfo => RawData != null 
            ? $"{RawData.ExposureTime_us / 1000.0:F1}ms, Gain {RawData.Gain}" 
            : "--";

        /// <summary>
        /// Binning mode
        /// </summary>
        public string BinningInfo => RawData != null ? $"{RawData.Binning}x{RawData.Binning}" : "--";

        /// <summary>
        /// Image type
        /// </summary>
        public string ImageTypeInfo => RawData != null ? RawData.ImageType.ToString() : "--";

        /// <summary>
        /// Temperature at capture (if available)
        /// </summary>
        public string TemperatureInfo => RawData?.Temperature_C.HasValue == true 
            ? $"{RawData.Temperature_C.Value:F1}°C" 
            : "--";

        /// <summary>
        /// Stretch info summary
        /// </summary>
        public string StretchInfo => $"{StretchProfile} ({StretchMin}-{StretchMax})";

        /// <summary>
        /// Thumbnail tooltip text (comprehensive)
        /// </summary>
        public string TooltipText
        {
            get
            {
                string baseText = 
                    $"#{Id} | {CaptureTime:HH:mm:ss}\n" +
                    $"{ImageSize} {ImageTypeInfo}\n" +
                    $"{ExposureInfo} | {BinningInfo}\n" +
                    $"Temp: {TemperatureInfo}\n" +
                    $"Stretch: {StretchProfile}";

                if (IsPartOfSeries)
                {
                    if (SeriesIndex == 0)
                        baseText += "\n?? Combined (Stacked)";
                    else
                        baseText += $"\n?? Series #{SeriesIndex}";
                }

                return baseText;
            }
        }

        /// <summary>
        /// Is this image currently selected in gallery?
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

