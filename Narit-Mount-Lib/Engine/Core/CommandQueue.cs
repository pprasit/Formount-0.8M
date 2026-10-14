using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ForkMount_WPF.Engine.Core
{
    /// <summary>
    /// Thread-safe Command Queue for Platinum Drive
    /// Supports priority-based command execution
    /// Part of Command Queue Architecture for non-blocking UI
    /// </summary>
    public class CommandQueue
    {
        private readonly ConcurrentQueue<DriveCommand> _normalQueue;
        private readonly ConcurrentQueue<DriveCommand> _highPriorityQueue;
        private readonly ConcurrentQueue<DriveCommand> _emergencyQueue;
        private readonly object _lockObject = new object();

        public CommandQueue()
        {
            _normalQueue = new ConcurrentQueue<DriveCommand>();
            _highPriorityQueue = new ConcurrentQueue<DriveCommand>();
            _emergencyQueue = new ConcurrentQueue<DriveCommand>();
        }

        /// <summary>
        /// Enqueue a command based on its priority
        /// </summary>
        public void Enqueue(DriveCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            lock (_lockObject)
            {
                switch (command.Priority)
                {
                    case 2: // Emergency
                        _emergencyQueue.Enqueue(command);
                        break;
                    case 1: // High Priority
                        _highPriorityQueue.Enqueue(command);
                        break;
                    default: // Normal
                        _normalQueue.Enqueue(command);
                        break;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[CommandQueue] Enqueued: {command} (Priority: {command.Priority})");
        }

        /// <summary>
        /// Try to dequeue next command (priority order: Emergency > High > Normal)
        /// </summary>
        public bool TryDequeue(out DriveCommand command)
        {
            lock (_lockObject)
            {
                // Emergency queue first
                if (_emergencyQueue.TryDequeue(out command))
                {
                    System.Diagnostics.Debug.WriteLine($"[CommandQueue] Dequeued EMERGENCY: {command}");
                    return true;
                }

                // High priority queue second
                if (_highPriorityQueue.TryDequeue(out command))
                {
                    System.Diagnostics.Debug.WriteLine($"[CommandQueue] Dequeued HIGH: {command}");
                    return true;
                }

                // Normal queue last
                if (_normalQueue.TryDequeue(out command))
                {
                    System.Diagnostics.Debug.WriteLine($"[CommandQueue] Dequeued NORMAL: {command}");
                    return true;
                }

                command = null;
                return false;
            }
        }

        /// <summary>
        /// Get total count of pending commands
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lockObject)
                {
                    return _emergencyQueue.Count + _highPriorityQueue.Count + _normalQueue.Count;
                }
            }
        }

        /// <summary>
        /// Clear all queues
        /// </summary>
        public void Clear()
        {
            lock (_lockObject)
            {
                while (_emergencyQueue.TryDequeue(out _)) { }
                while (_highPriorityQueue.TryDequeue(out _)) { }
                while (_normalQueue.TryDequeue(out _)) { }
            }

            System.Diagnostics.Debug.WriteLine("[CommandQueue] All queues cleared");
        }

        /// <summary>
        /// Get count by priority
        /// </summary>
        public (int emergency, int high, int normal) GetCountsByPriority()
        {
            lock (_lockObject)
            {
                return (_emergencyQueue.Count, _highPriorityQueue.Count, _normalQueue.Count);
            }
        }
    }
}
