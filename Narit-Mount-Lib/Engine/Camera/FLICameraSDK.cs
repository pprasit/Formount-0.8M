using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// P/Invoke wrapper for FLI (Finger Lakes Instrumentation) Camera SDK native DLL
    /// Ported from CCD_FLI project (FliSharp.FLI) to modern .NET 10
    /// 
    /// Native DLL files required:
    /// - Windows: libfli64.dll (x64)
    /// - Place in: dll\ folder (auto-copied to output by csproj)
    /// 
    /// Reference: CCD_FLI\FLI.cs (original FliSharp namespace)
    /// </summary>
    public class FLICameraSDK : IDisposable
    {
        #region Constants

        public const int MAX_STRING_LEN = 256;
        public const double COOLER_MAX_TEMP = 45;
        private const int INVALID_DEVICE = -1;
        private const string DllName = "libfli64";

        #endregion

        #region Enumerations

        [Flags]
        public enum FLI_DOMAIN : int
        {
            NONE = 0x00,
            PARALLEL_PORT = 0x01,
            USB = 0x02,
            SERIAL = 0x03,
            INET = 0x04,
            SERIAL_19200 = 0x05,
            SERIAL_1200 = 0x06,

            CAMERA = 0x100,
            FILTERWHEEL = 0x200,
            FOCUSER = 0x300,
            HS_FILTERWHEEL = 0x0f00,
            RAW = 0x500,
            ENUMERATE_BY_CONNECTION = 0x8000
        }

        [Flags]
        public enum FLI_FRAME_TYPE : int
        {
            NORMAL = 0,
            DARK = 1,
            FLOOD = 2,
            RBI_FLUSH = DARK | FLOOD
        }

        public enum FLI_BIT_DEPTH : int
        {
            MODE_8BIT = 0,
            MODE_16BIT = 1
        }

        public enum FLI_SHUTTER : int
        {
            CLOSE = 0x0000,
            OPEN = 0x0001,
            EXTERNAL_TRIGGER = 0x0002,
            EXTERNAL_TRIGGER_LOW = 0x0002,
            EXTERNAL_TRIGGER_HIGH = 0x0004,
            EXPOSURE_CONTROL = 0x0008
        }

        public enum FLI_BGFLUSH : int
        {
            STOP = 0x0000,
            START = 0x0001
        }

        public enum FLI_CHANNEL : int
        {
            INTERNAL = 0x0000,
            EXTERNAL = 0x0001,
            CCD = 0x0000,
            BASE = 0x0001
        }

        [Flags]
        public enum FLI_DEBUG : int
        {
            NONE = 0x00,
            INFO = 0x01,
            WARN = 0x02,
            FAIL = 0x04,
            IO = 0x08,
            ALL = INFO | WARN | FAIL
        }

        [Flags]
        public enum FLI_FAN_SPEED : int
        {
            OFF = 0x00,
            ON = unchecked((int)0xffffffff)
        }

        /// <summary>
        /// Camera status flags (from device status register)
        /// </summary>
        [Flags]
        public enum FLI_STATUS : uint
        {
            CAMERA_STATUS_UNKNOWN = 0xFFFFFFFF,
            CAMERA_STATUS_MASK = 0x00000003,
            CAMERA_STATUS_IDLE = 0x00,
            CAMERA_STATUS_WAITING_FOR_TRIGGER = 0x01,
            CAMERA_STATUS_EXPOSING = 0x02,
            CAMERA_STATUS_READING_CCD = 0x03,
            CAMERA_DATA_READY = 0x80000000
        }

        #endregion

        #region Structures

        /// <summary>
        /// Device information returned by FLIList
        /// </summary>
        public class FLI_DeviceName
        {
            public string FileName;
            public string ModelName;
            public string SerialNumber;
        }

        /// <summary>
        /// Internal struct used for marshaling strings from FLIList
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private class StringWrapper
        {
            public string s;
        }

        #endregion

        #region Members

        private IntPtr _dev = (IntPtr)INVALID_DEVICE;
        private bool _disposed = false;
        private int _visibleWidth = int.MinValue;

        #endregion

        #region Static APIs

        [DllImport(DllName)]
        private static extern int FLISetDebugLevel(string host, FLI_DEBUG level);
        public static void SetDebugLevel(string host, FLI_DEBUG level)
        {
            int status = FLISetDebugLevel(host, level);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIGetLibVersion(StringBuilder ver, int len);
        public static string GetLibVersion()
        {
            StringBuilder sb = new StringBuilder(MAX_STRING_LEN);
            int status = FLIGetLibVersion(sb, sb.MaxCapacity);
            if (0 != status)
                throw new Win32Exception(-status);
            return sb.ToString();
        }

        [DllImport(DllName)]
        private static extern int FLIList(FLI_DOMAIN domain, out IntPtr names);
        [DllImport(DllName)]
        private static extern int FLIFreeList(IntPtr names);

        /// <summary>
        /// Enumerate all FLI devices of the given domain
        /// </summary>
        public static FLI_DeviceName[] List(FLI_DOMAIN domain)
        {
            IntPtr namesHandle;
            int status = FLIList(domain, out namesHandle);
            if (0 != status)
                throw new Win32Exception(-status);

            var nameList = new System.Collections.Generic.List<FLI_DeviceName>();
            IntPtr p = namesHandle;

            while (IntPtr.Zero != p)
            {
                string s = ((StringWrapper)Marshal.PtrToStructure(p, typeof(StringWrapper))).s;
                if (null == s)
                    break;

                int delimPos = s.IndexOf(';');
                var dn = new FLI_DeviceName
                {
                    FileName = (-1 == delimPos ? s : s.Substring(0, delimPos)),
                    ModelName = (-1 == delimPos ? null : s.Substring(delimPos + 1))
                };
                nameList.Add(dn);
                p += sizeof(long);
            }

            FLIFreeList(namesHandle);
            return nameList.ToArray();
        }

        #endregion

        #region Device Open / Close / Dispose

        [DllImport(DllName)]
        private static extern int FLIOpen(out IntPtr dev, string name, FLI_DOMAIN domain);

        /// <summary>
        /// Open a connection to an FLI device
        /// </summary>
        public FLICameraSDK(string name, FLI_DOMAIN domain)
        {
            int status = FLIOpen(out _dev, name, domain);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIClose(IntPtr dev);

        public void Close()
        {
            if ((IntPtr)INVALID_DEVICE == _dev)
                return;

            int status = FLIClose(_dev);
            if (0 != status)
                throw new Win32Exception(-status);

            _dev = (IntPtr)INVALID_DEVICE;
        }

        ~FLICameraSDK()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            Close();
            _disposed = true;
        }

        #endregion

        #region Device Info APIs

        [DllImport(DllName)]
        private static extern int FLIGetModel(IntPtr dev, StringBuilder model, int len);
        public string GetModel()
        {
            StringBuilder sb = new StringBuilder(MAX_STRING_LEN);
            int status = FLIGetModel(_dev, sb, sb.MaxCapacity);
            if (0 != status) return null;
            return sb.ToString();
        }

        [DllImport(DllName)]
        private static extern int FLIGetSerialString(IntPtr dev, StringBuilder serial, int len);
        public string GetSerialString()
        {
            StringBuilder sb = new StringBuilder(MAX_STRING_LEN);
            int status = FLIGetSerialString(_dev, sb, sb.MaxCapacity);
            if (0 != status) return null;
            return sb.ToString();
        }

        [DllImport(DllName)]
        private static extern int FLIGetPixelSize(IntPtr dev, out double pixel_x, out double pixel_y);
        public void GetPixelSize(out double pixel_x, out double pixel_y)
        {
            int status = FLIGetPixelSize(_dev, out pixel_x, out pixel_y);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIGetHWRevision(IntPtr dev, out int hwrev);
        public int GetHWRevision()
        {
            int hwrev;
            int status = FLIGetHWRevision(_dev, out hwrev);
            if (0 != status)
                throw new Win32Exception(-status);
            return hwrev;
        }

        [DllImport(DllName)]
        private static extern int FLIGetFWRevision(IntPtr dev, out int fwrev);
        public int GetFWRevision()
        {
            int fwrev;
            int status = FLIGetFWRevision(_dev, out fwrev);
            if (0 != status)
                throw new Win32Exception(-status);
            return fwrev;
        }

        #endregion

        #region Sensor Area APIs

        [DllImport(DllName)]
        private static extern int FLIGetArrayArea(IntPtr dev, out int ul_x, out int ul_y, out int lr_x, out int lr_y);
        public void GetArrayArea(out int ul_x, out int ul_y, out int lr_x, out int lr_y)
        {
            int status = FLIGetArrayArea(_dev, out ul_x, out ul_y, out lr_x, out lr_y);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIGetVisibleArea(IntPtr dev, out int ul_x, out int ul_y, out int lr_x, out int lr_y);
        public void GetVisibleArea(out int ul_x, out int ul_y, out int lr_x, out int lr_y)
        {
            int status = FLIGetVisibleArea(_dev, out ul_x, out ul_y, out lr_x, out lr_y);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLISetImageArea(IntPtr dev, int ul_x, int ul_y, int lr_x, int lr_y);
        public void SetImageArea(int ul_x, int ul_y, int lr_x, int lr_y)
        {
            int status = FLISetImageArea(_dev, ul_x, ul_y, lr_x, lr_y);
            if (0 != status)
                throw new Win32Exception(-status);

            // Force row-width recalc after ROI or binning changes.
            _visibleWidth = int.MinValue;
        }

        #endregion

        #region Exposure APIs

        [DllImport(DllName)]
        private static extern int FLISetExposureTime(IntPtr dev, int exptime);
        /// <summary>
        /// Set exposure time in milliseconds
        /// </summary>
        public void SetExposureTime(int exptime_ms)
        {
            int status = FLISetExposureTime(_dev, exptime_ms);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLISetFrameType(IntPtr dev, FLI_FRAME_TYPE frametype);
        public void SetFrameType(FLI_FRAME_TYPE frametype)
        {
            int status = FLISetFrameType(_dev, frametype);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIExposeFrame(IntPtr dev);
        public void ExposeFrame()
        {
            int status = FLIExposeFrame(_dev);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLICancelExposure(IntPtr dev);
        public void CancelExposure()
        {
            int status = FLICancelExposure(_dev);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIGetExposureStatus(IntPtr dev, out int timeleft);
        /// <summary>
        /// Get remaining exposure time in milliseconds. Returns 0 when download is ready.
        /// </summary>
        public int GetExposureStatus()
        {
            int timeleft;
            int status = FLIGetExposureStatus(_dev, out timeleft);
            if (0 != status)
                throw new Win32Exception(-status);
            return timeleft;
        }

        /// <summary>
        /// Check if the camera exposure is complete and image data is ready to download
        /// </summary>
        public bool IsDownloadReady()
        {
            return GetExposureStatus() == 0;
        }

        /// <summary>
        /// Check if download is ready, returning remaining exposure time
        /// </summary>
        public bool IsDownloadReady(out int remainingTime_ms)
        {
            remainingTime_ms = GetExposureStatus();
            return remainingTime_ms == 0;
        }

        [DllImport(DllName)]
        private static extern int FLIGetDeviceStatus(IntPtr dev, out int status);
        /// <summary>
        /// Get device status register value
        /// </summary>
        public int GetDeviceStatus()
        {
            int devStatus;
            int status = FLIGetDeviceStatus(_dev, out devStatus);
            if (0 != status)
                throw new Win32Exception(-status);
            return devStatus;
        }

        [DllImport(DllName)]
        private static extern int FLIEndExposure(IntPtr dev);
        public void EndExposure()
        {
            int status = FLIEndExposure(_dev);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLITriggerExposure(IntPtr dev);
        public void TriggerExposure()
        {
            int status = FLITriggerExposure(_dev);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        #endregion

        #region Binning APIs

        [DllImport(DllName)]
        private static extern int FLISetHBin(IntPtr dev, int hbin);
        public void SetHBin(int hbin)
        {
            int status = FLISetHBin(_dev, hbin);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLISetVBin(IntPtr dev, int vbin);
        public void SetVBin(int vbin)
        {
            int status = FLISetVBin(_dev, vbin);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIGetReadoutDimensions(IntPtr dev, out int width, out int hoffset, out int hbin, out int height, out int voffset, out int vbin);
        public void GetReadoutDimensions(out int width, out int hoffset, out int hbin, out int height, out int voffset, out int vbin)
        {
            int status = FLIGetReadoutDimensions(_dev, out width, out hoffset, out hbin, out height, out voffset, out vbin);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        #endregion

        #region Image Download APIs

        [DllImport(DllName)]
        private static extern int FLIGrabRow(IntPtr dev, IntPtr buff, int width);

        /// <summary>
        /// Download a single row from the camera (16-bit)
        /// </summary>
        public void GrabRow(ushort[] buff)
        {
            GrabRow(buff, buff?.Length ?? 0);
        }

        public void GrabRow(ushort[] buff, int width)
        {
            if (buff == null)
                throw new ArgumentNullException(nameof(buff));

            if (buff.Length == 0)
                throw new ArgumentException("buff must not be empty", nameof(buff));

            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "width must be greater than zero");

            if (width > buff.Length)
                throw new ArgumentException($"width {width} exceeds buffer length {buff.Length}", nameof(width));

            GCHandle buffGch = GCHandle.Alloc(buff, GCHandleType.Pinned);
            IntPtr buffPtr = buffGch.AddrOfPinnedObject();

            try
            {
                _visibleWidth = width;

                int status = FLIGrabRow(_dev, buffPtr, width);
                if (0 != status)
                    throw new Win32Exception(-status);
            }
            finally
            {
                buffGch.Free();
            }
        }

        /// <summary>
        /// Download a single row from the camera (8-bit)
        /// </summary>
        public void GrabRow(byte[] buff)
        {
            GCHandle buffGch = GCHandle.Alloc(buff, GCHandleType.Pinned);
            IntPtr buffPtr = buffGch.AddrOfPinnedObject();

            try
            {
                int status = FLIGrabRow(_dev, buffPtr, buff.Length);
                if (0 != status)
                    throw new Win32Exception(-status);
            }
            finally
            {
                buffGch.Free();
            }
        }

        [DllImport(DllName)]
        private static extern int FLIGrabFrame(IntPtr dev, IntPtr buff, int buffsize, out int bytesgrabbed);

        /// <summary>
        /// Download entire frame from the camera (16-bit)
        /// </summary>
        public void GrabFrame(ushort[,] buff)
        {
            GCHandle buffGch = GCHandle.Alloc(buff, GCHandleType.Pinned);
            IntPtr buffPtr = buffGch.AddrOfPinnedObject();
            int bytesgrabbed;

            try
            {
                int status = FLIGrabFrame(_dev, buffPtr, buff.Length * sizeof(ushort), out bytesgrabbed);
                if (0 != status)
                    throw new Win32Exception(-status);
            }
            finally
            {
                buffGch.Free();
            }

            if (buff.Length * sizeof(ushort) != bytesgrabbed)
                throw new InvalidOperationException("bytesgrabbed != sizeof(buff)");
        }

        #endregion

        #region Temperature & Cooler APIs

        [DllImport(DllName)]
        private static extern int FLISetTemperature(IntPtr dev, double temperature);
        /// <summary>
        /// Set the CCD target temperature in degrees Celsius
        /// </summary>
        public void SetTemperature(double temperature)
        {
            int status = FLISetTemperature(_dev, temperature);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIGetTemperature(IntPtr dev, out double temperature);
        /// <summary>
        /// Get the current CCD temperature in degrees Celsius
        /// </summary>
        public double GetTemperature()
        {
            double temperature;
            int status = FLIGetTemperature(_dev, out temperature);
            if (0 != status)
                throw new Win32Exception(-status);
            return temperature;
        }

        [DllImport(DllName)]
        private static extern int FLIReadTemperature(IntPtr dev, FLI_CHANNEL channel, out double temperature);
        /// <summary>
        /// Read temperature from a specific channel (CCD or BASE)
        /// </summary>
        public double ReadTemperature(FLI_CHANNEL channel)
        {
            double temperature;
            int status = FLIReadTemperature(_dev, channel, out temperature);
            if (0 != status)
                throw new Win32Exception(-status);
            return temperature;
        }

        [DllImport(DllName)]
        private static extern int FLIGetCoolerPower(IntPtr dev, out double power);
        /// <summary>
        /// Get current cooler power percentage
        /// </summary>
        public double GetCoolerPower()
        {
            double power;
            int status = FLIGetCoolerPower(_dev, out power);
            if (0 != status)
                throw new Win32Exception(-status);
            return power;
        }

        #endregion

        #region Shutter & Flush APIs

        [DllImport(DllName)]
        private static extern int FLIControlShutter(IntPtr dev, FLI_SHUTTER shutter);
        public void ControlShutter(FLI_SHUTTER shutter)
        {
            int status = FLIControlShutter(_dev, shutter);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIControlBackgroundFlush(IntPtr dev, FLI_BGFLUSH bgflush);
        public void ControlBackgroundFlush(FLI_BGFLUSH bgflush)
        {
            int status = FLIControlBackgroundFlush(_dev, bgflush);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLISetNFlushes(IntPtr dev, int nflushes);
        public void SetNFlushes(int nflushes)
        {
            int status = FLISetNFlushes(_dev, nflushes);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLISetBitDepth(IntPtr dev, FLI_BIT_DEPTH bitdepth);
        public void SetBitDepth(FLI_BIT_DEPTH bitdepth)
        {
            int status = FLISetBitDepth(_dev, bitdepth);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        #endregion

        #region Camera Mode APIs

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int FLIGetCameraModeString(IntPtr dev, int mode_index, [MarshalAs(UnmanagedType.LPStr)] StringBuilder mode_string, int len);
        public string GetCameraModeString(int mode_index)
        {
            StringBuilder sb = new StringBuilder(MAX_STRING_LEN);
            int status = FLIGetCameraModeString(_dev, mode_index, sb, MAX_STRING_LEN);
            if (0 != status)
                return null;
            return sb.ToString();
        }

        [DllImport(DllName)]
        private static extern int FLIGetCameraMode(IntPtr dev, out int mode_index);
        public int GetCameraMode()
        {
            int mode_index;
            int status = FLIGetCameraMode(_dev, out mode_index);
            if (0 != status)
                throw new Win32Exception(-status);
            return mode_index;
        }

        [DllImport(DllName)]
        private static extern int FLISetCameraMode(IntPtr dev, int mode_index);
        public void SetCameraMode(int mode_index)
        {
            int status = FLISetCameraMode(_dev, mode_index);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        #endregion

        #region Fan & Lock APIs

        [DllImport(DllName)]
        private static extern int FLISetFanSpeed(IntPtr dev, FLI_FAN_SPEED fan_speed);
        public void SetFanSpeed(FLI_FAN_SPEED fan_speed)
        {
            int status = FLISetFanSpeed(_dev, fan_speed);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLILockDevice(IntPtr dev);
        public void LockDevice()
        {
            int status = FLILockDevice(_dev);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        [DllImport(DllName)]
        private static extern int FLIUnlockDevice(IntPtr dev);
        public void UnlockDevice()
        {
            int status = FLIUnlockDevice(_dev);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        #endregion

        #region TDI APIs

        [DllImport(DllName)]
        private static extern int FLISetTDI(IntPtr dev, int tdi_rate, int flags);
        public void SetTDI(int tdi_rate)
        {
            int status = FLISetTDI(_dev, tdi_rate, 0);
            if (0 != status)
                throw new Win32Exception(-status);
        }

        #endregion
    }
}
