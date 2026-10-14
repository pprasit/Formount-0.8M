namespace NARIT.Mount.API.Models
{
    /// <summary>
    /// User-facing telescope offset mode.
    /// This matches the offset mode selector in the main mount application.
    /// </summary>
    public enum OffsetType
    {
        /// <summary>Offset labels are interpreted as RA / Dec.</summary>
        RaDec = 0,

        /// <summary>Offset labels are interpreted as Alt / Azm.</summary>
        AltAzm = 1
    }

    /// <summary>
    /// Supported offset directions for the telescope offset pad.
    /// </summary>
    public enum OffsetDirection
    {
        N = 0,
        NE = 1,
        E = 2,
        SE = 3,
        S = 4,
        SW = 5,
        W = 6,
        NW = 7
    }

    /// <summary>
    /// Snapshot of the accumulated telescope offset.
    /// </summary>
    public class TelescopeOffsetInfo
    {
        /// <summary>Coordinate mode used when presenting the values.</summary>
        public OffsetType OffsetType { get; set; }

        /// <summary>Accumulated first-axis offset in arcseconds.</summary>
        public double Axis1_Arcsec { get; set; }

        /// <summary>Accumulated second-axis offset in arcseconds.</summary>
        public double Axis2_Arcsec { get; set; }

        /// <summary>User-facing first-axis label.</summary>
        public string Axis1Label { get; set; } = "";

        /// <summary>User-facing second-axis label.</summary>
        public string Axis2Label { get; set; } = "";
    }
}
