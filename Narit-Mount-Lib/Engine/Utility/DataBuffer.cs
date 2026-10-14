using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Thread-safe data buffer for high-speed data acquisition
    /// Stores data points from polling loop (200Hz) for UI consumption (100Hz)
    /// Prevents data loss for chart plotting
    /// 
    /// Time-based buffer: Keeps data for maximum 2 seconds
    /// Capacity-based backup: Maximum 500 points (2.5 seconds @ 200Hz)
    /// </summary>
    public class DataBuffer
    {
        #region Fields
        private readonly ConcurrentQueue<DataPoint> _buffer;
        private readonly int _maxCapacity;
        private readonly double _maxAgeSeconds;
        private DataPoint _latestValue;
        private readonly object _latestLock = new object();
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="maxAgeSeconds">Maximum age of data points in seconds (default: 2.0 seconds)</param>
        /// <param name="maxCapacity">Maximum buffer capacity as backup limit (default: 500 points)</param>
        public DataBuffer(double maxAgeSeconds = 2.0, int maxCapacity = 500)
        {
            _buffer = new ConcurrentQueue<DataPoint>();
            _maxAgeSeconds = maxAgeSeconds;
            _maxCapacity = maxCapacity;
            _latestValue = new DataPoint();
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// Add new data point to buffer (called from polling loop)
        /// Automatically removes old data points older than maxAgeSeconds
        /// Thread-safe
        /// </summary>
        public void Add(DataPoint point)
        {
            if (point == null)
                return;

            // Add to buffer
            _buffer.Enqueue(point);

            // Update latest value (thread-safe)
            lock (_latestLock)
            {
                _latestValue = point;
            }

            // Clean up old data (time-based)
            CleanOldData(point.Timestamp);

            // Also enforce capacity limit as backup
            while (_buffer.Count > _maxCapacity)
            {
                _buffer.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Remove data points older than maxAgeSeconds
        /// Called automatically when adding new data
        /// </summary>
        private void CleanOldData(DateTime currentTimestamp)
        {
            DateTime cutoffTime = currentTimestamp.AddSeconds(-_maxAgeSeconds);

            // Remove old data points
            while (_buffer.TryPeek(out DataPoint oldestPoint))
            {
                if (oldestPoint.Timestamp < cutoffTime)
                {
                    // Remove this old point
                    _buffer.TryDequeue(out _);
                }
                else
                {
                    // No more old points, stop
                    break;
                }
            }
        }

        /// <summary>
        /// Get latest value without removing from buffer
        /// Used for TextBox display
        /// Thread-safe
        /// </summary>
        public DataPoint GetLatest()
        {
            lock (_latestLock)
            {
                return _latestValue;
            }
        }

        /// <summary>
        /// Get all buffered data and clear buffer
        /// Used for chart plotting (consumes all pending data)
        /// Thread-safe
        /// </summary>
        public List<DataPoint> GetAllAndClear()
        {
            var result = new List<DataPoint>();

            while (_buffer.TryDequeue(out DataPoint point))
            {
                result.Add(point);
            }

            return result;
        }

        /// <summary>
        /// Get all buffered data without clearing
        /// Used for preview/inspection
        /// Thread-safe
        /// </summary>
        public List<DataPoint> GetAll()
        {
            return _buffer.ToList();
        }

        /// <summary>
        /// Get buffer count (number of pending data points)
        /// </summary>
        public int Count => _buffer.Count;

        /// <summary>
        /// Get maximum age setting (seconds)
        /// </summary>
        public double MaxAgeSeconds => _maxAgeSeconds;

        /// <summary>
        /// Get maximum capacity setting (points)
        /// </summary>
        public int MaxCapacity => _maxCapacity;

        /// <summary>
        /// Clear all buffered data
        /// </summary>
        public void Clear()
        {
            while (_buffer.TryDequeue(out _)) { }
        }

        #endregion
    }
}

