using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ForkMount_WPF.Engine.Info;

namespace ForkMount_WPF.Engine.Manager
{
    /// <summary>
    /// Thread-safe repository for captured astronomical images
    /// Supports searching by time, position (Az/Alt), and other criteria
    /// Used for Mount Calibration and Plate Solving
    /// </summary>
    public class CapturedImageRepository
    {
        #region Private Fields
        
        // Primary storage - indexed by ImageId
        private readonly ConcurrentDictionary<Guid, CapturedImageInfo> _imagesById;
        
        // Secondary indexes for fast lookup
        private readonly ConcurrentDictionary<DateTime, Guid> _imageIdsByTime;
        private readonly ConcurrentDictionary<string, List<Guid>> _imageIdsByPosition; // Key: "Az_Alt" rounded
        
        // Configuration
        private readonly double _positionTolerance_deg = 0.5; // Tolerance for position search
        private readonly int _maxImages = 1000; // Maximum images to keep in memory
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Total number of images in repository
        /// </summary>
        public int Count => _imagesById.Count;
        
        /// <summary>
        /// Total memory used by images (MB)
        /// </summary>
        public double TotalMemoryUsage_MB
        {
            get
            {
                return _imagesById.Values.Sum(img => img.GetImageSize_MB());
            }
        }
        
        /// <summary>
        /// Number of solved images
        /// </summary>
        public int SolvedCount => _imagesById.Values.Count(img => img.PlateSolveStatus == PlateSolveStatus.Solved);
        
        #endregion
        
        #region Constructor
        
        public CapturedImageRepository()
        {
            _imagesById = new ConcurrentDictionary<Guid, CapturedImageInfo>();
            _imageIdsByTime = new ConcurrentDictionary<DateTime, Guid>();
            _imageIdsByPosition = new ConcurrentDictionary<string, List<Guid>>();
        }
        
        #endregion
        
        #region Add / Remove
        
        /// <summary>
        /// Add image to repository
        /// </summary>
        public bool AddImage(CapturedImageInfo image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            
            try
            {
                // Check memory limit
                if (Count >= _maxImages)
                {
                    Debug.WriteLine($"[ImageRepository] ?? Maximum image count reached ({_maxImages}). Removing oldest image...");
                    RemoveOldestImage();
                }
                
                // Add to primary storage
                if (!_imagesById.TryAdd(image.ImageId, image))
                {
                    Debug.WriteLine($"[ImageRepository] ?? Image {image.ImageId} already exists");
                    return false;
                }
                
                // Add to time index
                _imageIdsByTime.TryAdd(image.CaptureTime, image.ImageId);
                
                // Add to position index
                string posKey = GetPositionKey(image.Azimuth_deg, image.Altitude_deg);
                _imageIdsByPosition.AddOrUpdate(
                    posKey,
                    new List<Guid> { image.ImageId },
                    (key, list) =>
                    {
                        list.Add(image.ImageId);
                        return list;
                    });
                
                Debug.WriteLine($"[ImageRepository] ? Added image {image.ImageId:N} " +
                               $"(Time={image.CaptureTime:HH:mm:ss}, Az={image.Azimuth_deg:F2}°, Alt={image.Altitude_deg:F2}°, " +
                               $"Size={image.GetImageSize_MB():F2}MB, Total={Count})");
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageRepository ERROR] Failed to add image: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Remove image from repository
        /// </summary>
        public bool RemoveImage(Guid imageId)
        {
            if (!_imagesById.TryRemove(imageId, out var image))
                return false;
            
            // Remove from time index
            _imageIdsByTime.TryRemove(image.CaptureTime, out _);
            
            // Remove from position index
            string posKey = GetPositionKey(image.Azimuth_deg, image.Altitude_deg);
            if (_imageIdsByPosition.TryGetValue(posKey, out var list))
            {
                list.Remove(imageId);
                if (list.Count == 0)
                {
                    _imageIdsByPosition.TryRemove(posKey, out _);
                }
            }
            
            Debug.WriteLine($"[ImageRepository] ??? Removed image {imageId:N}");
            return true;
        }
        
        /// <summary>
        /// Remove oldest image (by capture time)
        /// </summary>
        private void RemoveOldestImage()
        {
            var oldest = _imagesById.Values.OrderBy(img => img.CaptureTime).FirstOrDefault();
            if (oldest != null)
            {
                RemoveImage(oldest.ImageId);
            }
        }
        
        /// <summary>
        /// Clear all images
        /// </summary>
        public void Clear()
        {
            _imagesById.Clear();
            _imageIdsByTime.Clear();
            _imageIdsByPosition.Clear();
            Debug.WriteLine("[ImageRepository] ??? All images cleared");
        }
        
        #endregion
        
        #region Search - By ID
        
        /// <summary>
        /// Get image by ID
        /// </summary>
        public CapturedImageInfo GetImageById(Guid imageId)
        {
            _imagesById.TryGetValue(imageId, out var image);
            return image;
        }
        
        #endregion
        
        #region Search - By Time
        
        /// <summary>
        /// Get image by exact capture time
        /// </summary>
        public CapturedImageInfo GetImageByTime(DateTime captureTime)
        {
            if (_imageIdsByTime.TryGetValue(captureTime, out var imageId))
            {
                return GetImageById(imageId);
            }
            return null;
        }
        
        /// <summary>
        /// Get images within time range
        /// </summary>
        public List<CapturedImageInfo> GetImagesByTimeRange(DateTime startTime, DateTime endTime)
        {
            return _imagesById.Values
                .Where(img => img.CaptureTime >= startTime && img.CaptureTime <= endTime)
                .OrderBy(img => img.CaptureTime)
                .ToList();
        }
        
        /// <summary>
        /// Get image closest to specified time
        /// </summary>
        public CapturedImageInfo GetImageClosestToTime(DateTime targetTime)
        {
            return _imagesById.Values
                .OrderBy(img => Math.Abs((img.CaptureTime - targetTime).TotalSeconds))
                .FirstOrDefault();
        }
        
        #endregion
        
        #region Search - By Position
        
        /// <summary>
        /// Get images near specified position (Az/Alt)
        /// </summary>
        /// <param name="azimuth_deg">Target azimuth (degrees)</param>
        /// <param name="altitude_deg">Target altitude (degrees)</param>
        /// <param name="tolerance_deg">Search tolerance (degrees), default = 0.5°</param>
        public List<CapturedImageInfo> GetImagesByPosition(
            double azimuth_deg, 
            double altitude_deg, 
            double? tolerance_deg = null)
        {
            double tolerance = tolerance_deg ?? _positionTolerance_deg;
            
            return _imagesById.Values
                .Where(img =>
                {
                    double azDiff = Math.Abs(img.Azimuth_deg - azimuth_deg);
                    double altDiff = Math.Abs(img.Altitude_deg - altitude_deg);
                    
                    // Handle azimuth wraparound (0° = 360°)
                    if (azDiff > 180)
                        azDiff = 360 - azDiff;
                    
                    return azDiff <= tolerance && altDiff <= tolerance;
                })
                .OrderBy(img => 
                {
                    double azDiff = Math.Abs(img.Azimuth_deg - azimuth_deg);
                    double altDiff = Math.Abs(img.Altitude_deg - altitude_deg);
                    if (azDiff > 180) azDiff = 360 - azDiff;
                    return Math.Sqrt(azDiff * azDiff + altDiff * altDiff); // Euclidean distance
                })
                .ToList();
        }
        
        /// <summary>
        /// Get image closest to specified position
        /// </summary>
        public CapturedImageInfo GetImageClosestToPosition(double azimuth_deg, double altitude_deg)
        {
            return GetImagesByPosition(azimuth_deg, altitude_deg, tolerance_deg: 180).FirstOrDefault();
        }
        
        /// <summary>
        /// Generate position key for indexing
        /// Rounds Az/Alt to 0.1° precision
        /// </summary>
        private string GetPositionKey(double azimuth_deg, double altitude_deg)
        {
            int az = (int)Math.Round(azimuth_deg * 10); // 0.1° precision
            int alt = (int)Math.Round(altitude_deg * 10);
            return $"{az}_{alt}";
        }
        
        #endregion
        
        #region Search - By Solve Status
        
        /// <summary>
        /// Get all solved images
        /// </summary>
        public List<CapturedImageInfo> GetSolvedImages()
        {
            return _imagesById.Values
                .Where(img => img.PlateSolveStatus == PlateSolveStatus.Solved)
                .OrderBy(img => img.CaptureTime)
                .ToList();
        }
        
        /// <summary>
        /// Get all unsolved images
        /// </summary>
        public List<CapturedImageInfo> GetUnsolvedImages()
        {
            return _imagesById.Values
                .Where(img => img.PlateSolveStatus == PlateSolveStatus.NotSolved)
                .OrderBy(img => img.CaptureTime)
                .ToList();
        }
        
        /// <summary>
        /// Get all failed images
        /// </summary>
        public List<CapturedImageInfo> GetFailedImages()
        {
            return _imagesById.Values
                .Where(img => img.PlateSolveStatus == PlateSolveStatus.Failed)
                .OrderBy(img => img.CaptureTime)
                .ToList();
        }
        
        #endregion
        
        #region Search - By Quality
        
        /// <summary>
        /// Get images with quality score above threshold
        /// </summary>
        public List<CapturedImageInfo> GetImagesByQuality(double minQualityScore)
        {
            return _imagesById.Values
                .Where(img => img.QualityScore.HasValue && img.QualityScore.Value >= minQualityScore)
                .OrderByDescending(img => img.QualityScore)
                .ToList();
        }
        
        /// <summary>
        /// Get best quality images (top N)
        /// </summary>
        public List<CapturedImageInfo> GetBestQualityImages(int count = 10)
        {
            return _imagesById.Values
                .Where(img => img.QualityScore.HasValue)
                .OrderByDescending(img => img.QualityScore)
                .Take(count)
                .ToList();
        }
        
        #endregion
        
        #region Statistics
        
        /// <summary>
        /// Get repository statistics
        /// </summary>
        public string GetStatistics()
        {
            var stats = $"?? Image Repository Statistics:\n";
            stats += $"  Total Images: {Count}\n";
            stats += $"  Solved: {SolvedCount}\n";
            stats += $"  Unsolved: {_imagesById.Values.Count(img => img.PlateSolveStatus == PlateSolveStatus.NotSolved)}\n";
            stats += $"  Failed: {_imagesById.Values.Count(img => img.PlateSolveStatus == PlateSolveStatus.Failed)}\n";
            stats += $"  Memory Usage: {TotalMemoryUsage_MB:F2} MB\n";
            
            if (Count > 0)
            {
                var oldest = _imagesById.Values.Min(img => img.CaptureTime);
                var newest = _imagesById.Values.Max(img => img.CaptureTime);
                stats += $"  Time Range: {oldest:HH:mm:ss} - {newest:HH:mm:ss}\n";
                
                var avgQuality = _imagesById.Values
                    .Where(img => img.QualityScore.HasValue)
                    .Select(img => img.QualityScore.Value)
                    .DefaultIfEmpty(0)
                    .Average();
                stats += $"  Avg Quality: {avgQuality:F1}\n";
            }
            
            return stats;
        }
        
        #endregion
        
        #region Get All
        
        /// <summary>
        /// Get all images (ordered by capture time)
        /// </summary>
        public List<CapturedImageInfo> GetAllImages()
        {
            return _imagesById.Values
                .OrderBy(img => img.CaptureTime)
                .ToList();
        }
        
        /// <summary>
        /// Get recent images (last N)
        /// </summary>
        public List<CapturedImageInfo> GetRecentImages(int count = 10)
        {
            return _imagesById.Values
                .OrderByDescending(img => img.CaptureTime)
                .Take(count)
                .ToList();
        }
        
        #endregion
    }
}
