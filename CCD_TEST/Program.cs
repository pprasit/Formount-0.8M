using System;
using System.Windows.Forms;

namespace CCD_TEST
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// NOTE: CCD_RUNNING is disabled — requires DEVICE_PACKET, TRTQueue, Lidgren.Network
        /// from external TRTDaemon repo. Restore those DLLs to re-enable.
        /// Original code:
        ///   AppSetting appSetting = new AppSetting();
        ///   appSetting.LoadSetting();
        ///   Application.Run(new CCD_RUNNING(appSetting.DATA.CAMERA_SERIAL, appSetting.DATA.M3Port, appSetting.DATA.HOSTING_IP, appSetting.DATA.HOSTING_PORT));
        /// </summary>
        [STAThread]
        static void Main()
        {
            MessageBox.Show(
                "CCD_RUNNING is currently disabled.\nRestore DEVICE_PACKET, TRTQueue, Lidgren.Network DLLs to enable.",
                "CCD_TEST",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
