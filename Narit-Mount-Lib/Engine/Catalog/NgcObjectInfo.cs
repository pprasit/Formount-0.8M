using System;
using System.ComponentModel;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// NGC/IC Object Information for Deep Sky Tracking
    /// Maps to MongoDB collection "NGC" in database "NEOs"
    /// </summary>
    [BsonIgnoreExtraElements]
    public class NgcObjectInfo : INotifyPropertyChanged
    {
        private string _name;
        private string _type;
        private double _rightAscension;
        private double _declination;
        private string _constellation;
        private string _majAx;
        private string _minAx;
        private string _posAng;
        private string _bMag;
        private string _vMag;
        private string _jMag;
        private string _hMag;
        private string _kMag;
        private string _surfBr;
        private string _hubble;
        private string _pax;
        private string _pmRA;
        private string _pmDec;
        private string _radVel;
        private string _redshift;
        private string _cstarUMag;
        private string _cstarBMag;
        private string _cstarVMag;
        private string _messier;
        private double _azimuth;
        private double _altitude;
        private bool _isVisible;
        private bool _isCurrentTarget;

        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("Name")]
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        [BsonElement("Type")]
        public string Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(nameof(Type)); OnPropertyChanged(nameof(TypeDescription)); }
        }

        [BsonElement("RA")]
        public string RAString { get; set; }

        [BsonElement("Dec")]
        public string DecString { get; set; }

        [BsonElement("Const")]
        public string Constellation
        {
            get => _constellation;
            set { _constellation = value; OnPropertyChanged(nameof(Constellation)); }
        }

        [BsonElement("MajAx")]
        public string MajAx
        {
            get => _majAx;
            set { _majAx = value; OnPropertyChanged(nameof(MajAx)); }
        }

        [BsonElement("MinAx")]
        public string MinAx
        {
            get => _minAx;
            set { _minAx = value; OnPropertyChanged(nameof(MinAx)); }
        }

        [BsonElement("PosAng")]
        public string PosAng
        {
            get => _posAng;
            set { _posAng = value; OnPropertyChanged(nameof(PosAng)); }
        }

        [BsonElement("B-Mag")]
        public string BMag
        {
            get => _bMag;
            set { _bMag = value; OnPropertyChanged(nameof(BMag)); }
        }

        [BsonElement("V-Mag")]
        public string VMag
        {
            get => _vMag;
            set { _vMag = value; OnPropertyChanged(nameof(VMag)); OnPropertyChanged(nameof(Magnitude)); }
        }

        [BsonElement("J-Mag")]
        public string JMag
        {
            get => _jMag;
            set { _jMag = value; OnPropertyChanged(nameof(JMag)); }
        }

        [BsonElement("H-Mag")]
        public string HMag
        {
            get => _hMag;
            set { _hMag = value; OnPropertyChanged(nameof(HMag)); }
        }

        [BsonElement("K-Mag")]
        public string KMag
        {
            get => _kMag;
            set { _kMag = value; OnPropertyChanged(nameof(KMag)); }
        }

        [BsonElement("SurfBr")]
        public string SurfBr
        {
            get => _surfBr;
            set { _surfBr = value; OnPropertyChanged(nameof(SurfBr)); }
        }

        [BsonElement("Hubble")]
        public string Hubble
        {
            get => _hubble;
            set { _hubble = value; OnPropertyChanged(nameof(Hubble)); }
        }

        [BsonElement("Pax")]
        public string Pax
        {
            get => _pax;
            set { _pax = value; OnPropertyChanged(nameof(Pax)); }
        }

        [BsonElement("Pm-RA")]
        public string PmRA
        {
            get => _pmRA;
            set { _pmRA = value; OnPropertyChanged(nameof(PmRA)); }
        }

        [BsonElement("Pm-Dec")]
        public string PmDec
        {
            get => _pmDec;
            set { _pmDec = value; OnPropertyChanged(nameof(PmDec)); }
        }

        [BsonElement("RadVel")]
        public string RadVel
        {
            get => _radVel;
            set { _radVel = value; OnPropertyChanged(nameof(RadVel)); }
        }

        [BsonElement("Redshift")]
        public string Redshift
        {
            get => _redshift;
            set { _redshift = value; OnPropertyChanged(nameof(Redshift)); }
        }

        [BsonElement("Cstar U-Mag")]
        public string CstarUMag
        {
            get => _cstarUMag;
            set { _cstarUMag = value; OnPropertyChanged(nameof(CstarUMag)); }
        }

        [BsonElement("Cstar B-Mag")]
        public string CstarBMag
        {
            get => _cstarBMag;
            set { _cstarBMag = value; OnPropertyChanged(nameof(CstarBMag)); }
        }

        [BsonElement("Cstar V-Mag")]
        public string CstarVMag
        {
            get => _cstarVMag;
            set { _cstarVMag = value; OnPropertyChanged(nameof(CstarVMag)); }
        }

        [BsonElement("M")]
        public string Messier
        {
            get => _messier;
            set { _messier = value; OnPropertyChanged(nameof(Messier)); OnPropertyChanged(nameof(DisplayName)); }
        }

        // Computed properties (not from MongoDB)
        [BsonIgnore]
        public double RightAscension
        {
            get => _rightAscension;
            set { _rightAscension = value; OnPropertyChanged(nameof(RightAscension)); OnPropertyChanged(nameof(RAFormatted)); }
        }

        [BsonIgnore]
        public double Declination
        {
            get => _declination;
            set { _declination = value; OnPropertyChanged(nameof(Declination)); OnPropertyChanged(nameof(DecFormatted)); }
        }

        [BsonIgnore]
        public double Azimuth
        {
            get => _azimuth;
            set { _azimuth = value; OnPropertyChanged(nameof(Azimuth)); }
        }

        [BsonIgnore]
        public double Altitude
        {
            get => _altitude;
            set { _altitude = value; OnPropertyChanged(nameof(Altitude)); }
        }

        [BsonIgnore]
        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); }
        }

        [BsonIgnore]
        public bool IsCurrentTarget
        {
            get => _isCurrentTarget;
            set { _isCurrentTarget = value; OnPropertyChanged(nameof(IsCurrentTarget)); }
        }

        /// <summary>
        /// Display name: Messier number if available, otherwise NGC/IC name
        /// </summary>
        [BsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Messier))
                    return $"M{Messier}";
                return Name ?? "Unknown";
            }
        }

        /// <summary>
        /// Get magnitude as double (uses V-Mag, falls back to B-Mag)
        /// </summary>
        [BsonIgnore]
        public double Magnitude
        {
            get
            {
                if (double.TryParse(VMag, out double vMag))
                    return vMag;
                if (double.TryParse(BMag, out double bMag))
                    return bMag;
                return 99.0; // Unknown magnitude
            }
        }

        /// <summary>
        /// Formatted RA string
        /// </summary>
        [BsonIgnore]
        public string RAFormatted
        {
            get
            {
                int h = (int)RightAscension;
                int m = (int)((RightAscension - h) * 60);
                double s = ((RightAscension - h) * 60 - m) * 60;
                return $"{h:00}:{m:00}:{s:00.0}";
            }
        }

        /// <summary>
        /// Formatted Dec string
        /// </summary>
        [BsonIgnore]
        public string DecFormatted
        {
            get
            {
                string sign = Declination >= 0 ? "+" : "-";
                double absDeg = Math.Abs(Declination);
                int d = (int)absDeg;
                int m = (int)((absDeg - d) * 60);
                double s = ((absDeg - d) * 60 - m) * 60;
                return $"{sign}{d:00}:{m:00}:{s:00.0}";
            }
        }

        /// <summary>
        /// Object type description
        /// </summary>
        [BsonIgnore]
        public string TypeDescription
        {
            get
            {
                return Type switch
                {
                    "G" => "Galaxy",
                    "GC" => "Globular Cluster",
                    "OC" => "Open Cluster",
                    "PN" => "Planetary Nebula",
                    "Nb" => "Nebula",
                    "C+N" => "Cluster + Nebula",
                    "*" => "Star",
                    "**" => "Double Star",
                    "*Ass" => "Star Association",
                    "Gx" => "Galaxy",
                    "SNR" => "Supernova Remnant",
                    "RfN" => "Reflection Nebula",
                    "EmN" => "Emission Nebula",
                    "HII" => "HII Region",
                    _ => Type ?? "Unknown"
                };
            }
        }

        /// <summary>
        /// Parse RA string from MongoDB format (e.g., "00:08:27.05") to hours
        /// </summary>
        public void ParseCoordinates()
        {
            // Parse RA (format: "HH:MM:SS.ss")
            if (!string.IsNullOrEmpty(RAString))
            {
                RightAscension = ParseHMS(RAString);
            }

            // Parse Dec (format: "+DD:MM:SS.s" or "-DD:MM:SS.s")
            if (!string.IsNullOrEmpty(DecString))
            {
                Declination = ParseDMS(DecString);
            }
        }

        private double ParseHMS(string hms)
        {
            try
            {
                var parts = hms.Replace(":", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    double h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    double m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    double s = parts.Length >= 3 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
                    return h + m / 60.0 + s / 3600.0;
                }
            }
            catch { }
            return 0;
        }

        private double ParseDMS(string dms)
        {
            try
            {
                bool isNegative = dms.StartsWith("-");
                string abs = dms.TrimStart('+', '-');
                var parts = abs.Replace(":", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    double d = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    double m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    double s = parts.Length >= 3 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
                    double result = d + m / 60.0 + s / 3600.0;
                    return isNegative ? -result : result;
                }
            }
            catch { }
            return 0;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
