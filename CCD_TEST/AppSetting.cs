using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace CCD_TEST
{
    public class AppSetting
    {
        public class Settings
        {
            public string CAMERA_SERIAL { get; set; }
            public int M3Port { get; set; }
            public string HOSTING_IP { get; set; } = "127.0.0.1";
            public int HOSTING_PORT { get; set; } = 65001;
        }

        public Settings DATA = null;
        private string SettingPath = null;

        public AppSetting()
        {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            SettingPath = Path.Combine(assemblyFolder, "app_config.json");
        }

        public bool LoadSetting()
        {
            DATA = new Settings();
            string JsonString = "";

            if (!File.Exists(SettingPath))
            {
                throw new FileNotFoundException(SettingPath);
            }

            try
            {
                using (StreamReader r = new StreamReader(SettingPath))
                {
                    JsonString = r.ReadToEnd();
                }

                if (JsonString != "")
                {
                    DATA = JsonConvert.DeserializeObject<Settings>(JsonString);
                }
                else
                {
                    File.Create(SettingPath).Close();
                    SaveSetting();
                }
            }
            catch
            {
                throw new FileLoadException("Error Loadding Setting");
            }

            return true;
        }

        public void SaveSetting()
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(SettingPath))
                {
                    String DataJson = JsonConvert.SerializeObject(DATA, Formatting.Indented);
                    sw.WriteLine(DataJson);
                    sw.Close();
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Save configulation file error !");
            }
        }
    }
}
