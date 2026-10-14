using System;
using System.Runtime.InteropServices;

namespace ForkMount_WPF.Engine.Camera
{
    /// <summary>
    /// P/Invoke wrapper for ZWO ASI Camera SDK native DLL
    /// SDK Version: 1.36+ (compatible with ASI120, ASI224, ASI294, ASI533, ASI2600, etc.)
    /// 
    /// Native DLL files required:
    /// - Windows: ASICamera2.dll (x64)
    /// - Place in: bin\Debug\net10.0-windows\ or project root
    /// 
    /// Download SDK from: https://astronomy-imaging-camera.com/software-drivers
    /// </summary>
    public static class ZWOASICameraSDK
    {
        private const string DllName = "ASICamera2";

        #region Enumerations

        public enum ASI_BAYER_PATTERN
        {
            ASI_BAYER_RG = 0,
            ASI_BAYER_BG = 1,
            ASI_BAYER_GR = 2,
            ASI_BAYER_GB = 3
        }

        public enum ASI_IMG_TYPE
        {
            ASI_IMG_RAW8 = 0,
            ASI_IMG_RGB24 = 1,
            ASI_IMG_RAW16 = 2,
            ASI_IMG_Y8 = 3
        }

        public enum ASI_GUIDE_DIRECTION
        {
            ASI_GUIDE_NORTH = 0,
            ASI_GUIDE_SOUTH = 1,
            ASI_GUIDE_EAST = 2,
            ASI_GUIDE_WEST = 3
        }

        public enum ASI_FLIP_STATUS
        {
            ASI_FLIP_NONE = 0,
            ASI_FLIP_HORIZ = 1,
            ASI_FLIP_VERT = 2,
            ASI_FLIP_BOTH = 3
        }

        public enum ASI_ERROR_CODE
        {
            ASI_SUCCESS = 0,
            ASI_ERROR_INVALID_INDEX = 1,
            ASI_ERROR_INVALID_ID = 2,
            ASI_ERROR_INVALID_CONTROL_TYPE = 3,
            ASI_ERROR_CAMERA_CLOSED = 4,
            ASI_ERROR_CAMERA_REMOVED = 5,
            ASI_ERROR_INVALID_PATH = 6,
            ASI_ERROR_INVALID_FILEFORMAT = 7,
            ASI_ERROR_INVALID_SIZE = 8,
            ASI_ERROR_INVALID_IMGTYPE = 9,
            ASI_ERROR_OUTOF_BOUNDARY = 10,
            ASI_ERROR_TIMEOUT = 11,
            ASI_ERROR_INVALID_SEQUENCE = 12,
            ASI_ERROR_BUFFER_TOO_SMALL = 13,
            ASI_ERROR_VIDEO_MODE_ACTIVE = 14,
            ASI_ERROR_EXPOSURE_IN_PROGRESS = 15,
            ASI_ERROR_GENERAL_ERROR = 16,
            ASI_ERROR_INVALID_MODE = 17,
            ASI_ERROR_END = 18
        }

        public enum ASI_BOOL
        {
            ASI_FALSE = 0,
            ASI_TRUE = 1
        }

        public enum ASI_CAMERA_MODE
        {
            ASI_MODE_NORMAL = 0,
            ASI_MODE_TRIG_SOFT_EDGE = 1,
            ASI_MODE_TRIG_RISE_EDGE = 2,
            ASI_MODE_TRIG_FALL_EDGE = 3,
            ASI_MODE_TRIG_SOFT_LEVEL = 4,
            ASI_MODE_TRIG_HIGH_LEVEL = 5,
            ASI_MODE_TRIG_LOW_LEVEL = 6,
            ASI_MODE_END = -1
        }

        public enum ASI_EXPOSURE_STATUS
        {
            ASI_EXP_IDLE = 0,
            ASI_EXP_WORKING = 1,
            ASI_EXP_SUCCESS = 2,
            ASI_EXP_FAILED = 3
        }

        public enum ASI_CONTROL_TYPE
        {
            ASI_GAIN = 0,
            ASI_EXPOSURE = 1,
            ASI_GAMMA = 2,
            ASI_WB_R = 3,
            ASI_WB_B = 4,
            ASI_OFFSET = 5,
            ASI_BANDWIDTHOVERLOAD = 6,
            ASI_OVERCLOCK = 7,
            ASI_TEMPERATURE = 8,
            ASI_FLIP = 9,
            ASI_AUTO_MAX_GAIN = 10,
            ASI_AUTO_MAX_EXP = 11,
            ASI_AUTO_TARGET_BRIGHTNESS = 12,
            ASI_HARDWARE_BIN = 13,
            ASI_HIGH_SPEED_MODE = 14,
            ASI_COOLER_POWER_PERC = 15,
            ASI_TARGET_TEMP = 16,
            ASI_COOLER_ON = 17,
            ASI_MONO_BIN = 18,
            ASI_FAN_ON = 19,
            ASI_PATTERN_ADJUST = 20,
            ASI_ANTI_DEW_HEATER = 21
        }

        #endregion

        #region Structures

        /// <summary>
        /// ASI Camera Information structure
        /// [CRITICAL FIX] Changed Pack from 1 to default (8) to match ZWO SDK native alignment
        /// Original ASICamera2.h struct uses default compiler packing
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct ASI_CAMERA_INFO
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string Name;

            public int CameraID;
            public int MaxHeight;
            public int MaxWidth;
            public ASI_BOOL IsColorCam;
            public ASI_BAYER_PATTERN BayerPattern;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public int[] SupportedBins;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public ASI_IMG_TYPE[] SupportedVideoFormat;

            public double PixelSize;
            public ASI_BOOL MechanicalShutter;
            public ASI_BOOL ST4Port;
            public ASI_BOOL IsCoolerCam;
            public ASI_BOOL IsUSB3Host;
            public ASI_BOOL IsUSB3Camera;
            public float ElecPerADU;
            public int BitDepth;
            public ASI_BOOL IsTriggerCam;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Unused;

            public string GetName()
            {
                return Name?.TrimEnd('\0') ?? "";
            }
        }

        /// <summary>
        /// ASI Control Capabilities structure
        /// [CRITICAL FIX] Changed Pack from 1 to default to match ZWO SDK native alignment
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct ASI_CONTROL_CAPS
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string Name;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;

            public int MaxValue;
            public int MinValue;
            public int DefaultValue;
            public ASI_BOOL IsAutoSupported;
            public ASI_BOOL IsWritable;
            public ASI_CONTROL_TYPE ControlType;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Unused;

            public string GetName()
            {
                return Name?.TrimEnd('\0') ?? "";
            }

            public string GetDescription()
            {
                return Description?.TrimEnd('\0') ?? "";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ASI_ID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] id;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ASI_SUPPORTED_MODE
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public ASI_CAMERA_MODE[] SupportedCameraMode;
        }

        #endregion

        #region Native DLL Imports

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ASIGetNumOfConnectedCameras();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetCameraProperty(out ASI_CAMERA_INFO pASICameraInfo, int iCameraIndex);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetCameraPropertyByID(int iCameraID, out ASI_CAMERA_INFO pASICameraInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIOpenCamera(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIInitCamera(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASICloseCamera(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetNumOfControls(int iCameraID, out int piNumberOfControls);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetControlCaps(int iCameraID, int iControlIndex, out ASI_CONTROL_CAPS pControlCaps);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetControlValue(int iCameraID, ASI_CONTROL_TYPE ControlType, out int plValue, out ASI_BOOL pbAuto);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISetControlValue(int iCameraID, ASI_CONTROL_TYPE ControlType, int lValue, ASI_BOOL bAuto);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISetROIFormat(int iCameraID, int iWidth, int iHeight, int iBin, ASI_IMG_TYPE Img_type);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetROIFormat(int iCameraID, out int piWidth, out int piHeight, out int piBin, out ASI_IMG_TYPE pImg_type);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISetStartPos(int iCameraID, int iStartX, int iStartY);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetStartPos(int iCameraID, out int piStartX, out int piStartY);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetDroppedFrames(int iCameraID, out int piDropFrames);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIEnableDarkSubtract(int iCameraID, [MarshalAs(UnmanagedType.LPStr)] string strBMPPath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIDisableDarkSubtract(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIStartVideoCapture(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIStopVideoCapture(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetVideoData(int iCameraID, IntPtr pBuffer, int lBuffSize, int iWaitms);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIPulseGuideOn(int iCameraID, ASI_GUIDE_DIRECTION direction);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIPulseGuideOff(int iCameraID, ASI_GUIDE_DIRECTION direction);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIStartExposure(int iCameraID, ASI_BOOL bIsDark);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIStopExposure(int iCameraID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetExpStatus(int iCameraID, out ASI_EXPOSURE_STATUS pExpStatus);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetDataAfterExp(int iCameraID, IntPtr pBuffer, int lBuffSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetID(int iCameraID, out ASI_ID pID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISetID(int iCameraID, ASI_ID ID);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetGainOffset(int iCameraID, out int pOffset_HighestDR, out int pOffset_UnityGain, out int pGain_LowestRN, out int pOffset_LowestRN);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetSDKVersion();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetCameraSupportMode(int iCameraID, out ASI_SUPPORTED_MODE pSupportedMode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetCameraMode(int iCameraID, out ASI_CAMERA_MODE mode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISetCameraMode(int iCameraID, ASI_CAMERA_MODE mode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISendSoftTrigger(int iCameraID, ASI_BOOL bStart);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetSerialNumber(int iCameraID, out ASI_ID pSN);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASISetTriggerOutputIOConf(int iCameraID, int pin, ASI_BOOL bPinHigh, int lDelay, int lDuration);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ASI_ERROR_CODE ASIGetTriggerOutputIOConf(int iCameraID, int pin, out ASI_BOOL bPinHigh, out int lDelay, out int lDuration);

        #endregion
    }
}
