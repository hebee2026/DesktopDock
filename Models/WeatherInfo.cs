using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace DesktopDock.Models
{
    public sealed class ShortcutItem
    {
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public bool IsFolder { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public BitmapSource Icon { get; set; }

        /// <summary>界面上对应的磁贴（Views.ShortcutTile），用于异步补图标</summary>
        public object Tile { get; set; }
    }

    public sealed class DailyWeather
    {
        public string Label { get; set; }
        public int Code { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
        public int? PrecipProb { get; set; }
    }

    public sealed class WeatherInfo
    {
        public string City { get; set; }
        public string Region { get; set; }
        public double Temp { get; set; }
        public double Feels { get; set; }
        public int Humidity { get; set; }
        public double Wind { get; set; }
        public int Code { get; set; }
        public bool IsDay { get; set; } = true;
        public double High { get; set; }
        public double Low { get; set; }
        public List<DailyWeather> Daily { get; set; } = new List<DailyWeather>();
        public DateTime UpdatedAt { get; set; }
        public string Error { get; set; }

        public static WeatherInfo Failure(string message) => new WeatherInfo
        {
            City = "--",
            Error = message,
            UpdatedAt = DateTime.Now
        };
    }
}
