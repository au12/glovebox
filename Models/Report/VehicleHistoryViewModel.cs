namespace CarCareTracker.Models
{
    public class VehicleHistoryViewModel
    {
        public Vehicle VehicleData { get; set; }
        public List<GenericReportModel> VehicleHistory { get; set; }
        public ReportParameter ReportParameters { get; set; }
        public string Odometer { get; set; } = string.Empty;
        public string MPG { get; set; } = string.Empty;
        public decimal TotalCost { get; set; }
        public decimal TotalGasCost { get; set; }
        public string DaysOwned { get; set; } = string.Empty;
        public string DistanceTraveled { get; set; } = string.Empty;
        public decimal TotalCostPerMile { get; set; }
        public decimal TotalGasCostPerMile { get; set; }
        public string DistanceUnit { get; set; } = string.Empty;
        public decimal TotalDepreciation { get; set; }
        public decimal DepreciationPerDay { get; set; }
        public decimal DepreciationPerMile { get; set; }
        public string StartDate { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        //Glovebox report additions
        public DateTime GeneratedOn { get; set; } = DateTime.Now;
        public bool ShowCosts { get; set; } = true;
        /// <summary>Short distance unit for display ("mi", "km" or "h").</summary>
        public string UnitLabel { get; set; } = "mi";
        public int MaxOdometer { get; set; }
        public int DistanceTraveledValue { get; set; }
        public DateTime? FirstRecordDate { get; set; }
        public DateTime? LastRecordDate { get; set; }
        /// <summary>Average fuel economy rounded for display, empty when there is no fuel data.</summary>
        public string FuelEconomy { get; set; } = string.Empty;
        public string FuelEconomyUnit { get; set; } = string.Empty;
        public List<DueItem> DueItems { get; set; } = new List<DueItem>();
        public List<ReportLastDoneItem> LastDone { get; set; } = new List<ReportLastDoneItem>();
        public List<ReportCostTotal> CategoryTotals { get; set; } = new List<ReportCostTotal>();
        public List<ReportCostTotal> YearTotals { get; set; } = new List<ReportCostTotal>();
    }
    public class ReportLastDoneItem
    {
        public string Description { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public int Odometer { get; set; }
        public string Since { get; set; } = string.Empty;
    }
    public class ReportCostTotal
    {
        public string Label { get; set; } = string.Empty;
        public ImportMode? DataType { get; set; }
        public int Year { get; set; }
        public int Count { get; set; }
        public decimal Cost { get; set; }
    }
}
