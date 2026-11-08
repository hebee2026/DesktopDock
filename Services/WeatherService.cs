using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopDock.Models;

namespace DesktopDock.Services
{
    /// <summary>WMO 天气代码 → 中文描述 / 符号 / 配色</summary>
    public static class WeatherCodes
    {
        public static (string Text, string Glyph, string Color) Describe(int code, bool isDay)
        {
            switch (code)
            {
                case 0: return ("晴", isDay ? "☀" : "☾", "#FFD479");
                case 1: return ("晴间少云", isDay ? "☀" : "☾", "#FFD479");
                case 2: return ("局部多云", "⛅", "#EBD9A8");
                case 3: return ("阴", "☁", "#C9D6E3");
                case 45:
                case 48: return ("雾", "≋", "#B9C6D2");
                case 51:
                case 53:
                case 55: return ("毛毛雨", "☂", "#8FD0FF");
                case 56:
                case 57: return ("冻毛毛雨", "☂", "#A8DEFF");
                case 61: return ("小雨", "☂", "#8FD0FF");
                case 63: return ("中雨", "☂", "#7CC5FF");
                case 65: return ("大雨", "☔", "#5FB4F5");
                case 66:
                case 67: return ("冻雨", "☔", "#9CD8FF");
                case 71: return ("小雪", "❄", "#D6ECFF");
                case 73: return ("中雪", "❄", "#D6ECFF");
                case 75: return ("大雪", "❄", "#E8F5FF");
                case 77: return ("雪粒", "❄", "#D6ECFF");
                case 80: return ("阵雨", "☂", "#8FD0FF");
                case 81: return ("强阵雨", "☂", "#6FBEFA");
                case 82: return ("暴雨", "☔", "#5FB4F5");
                case 85:
                case 86: return ("阵雪", "❄", "#D6ECFF");
                case 95: return ("雷阵雨", "⚡", "#FFE066");
                case 96:
                case 99: return ("雷暴冰雹", "⚡", "#FFD166");
                default: return ("未知", "◌", "#C9D6E3");
            }
        }
    }

    public sealed class WeatherService
    {
        private static string CachePath => System.IO.Path.Combine(SettingsStore.DataDir, "weather.json");

        private readonly System.Net.Http.HttpClient _http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        private string _lastGeoQuery;
        private (double Lat, double Lon, string City, string Region)? _lastGeo;

        public WeatherService()
        {
            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopDock/1.0");
            }
            catch
            {
                // 忽略
            }
        }

        public async Task<WeatherInfo> FetchAsync(AppSettings settings)
        {
            try
            {
                var geo = await ResolveCityAsync(settings.WeatherCity).ConfigureAwait(false);
                if (geo == null)
                    return WeatherInfo.Failure("找不到城市：" + settings.WeatherCity);

                string unit = settings.UseCelsius ? "celsius" : "fahrenheit";
                string url = "https://api.open-meteo.com/v1/forecast"
                             + "?latitude=" + geo.Value.Lat.ToString("0.####", CultureInfo.InvariantCulture)
                             + "&longitude=" + geo.Value.Lon.ToString("0.####", CultureInfo.InvariantCulture)
                             + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m"
                             + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                             + "&timezone=auto&forecast_days=5&temperature_unit=" + unit;

                string json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var info = Parse(json, geo.Value.City, geo.Value.Region);
                SaveCache(info);
                return info;
            }
            catch (Exception ex)
            {
                var cached = LoadCache();
                if (cached != null)
                {
                    cached.Error = "离线缓存：" + ex.Message;
                    return cached;
                }

                return WeatherInfo.Failure("天气获取失败：" + ex.Message);
            }
        }

        private async Task<(double Lat, double Lon, string City, string Region)?> ResolveCityAsync(string city)
        {
            city = (city ?? "").Trim();
            if (city.Length == 0) city = "上海";

            if (_lastGeo != null && string.Equals(_lastGeoQuery, city, StringComparison.OrdinalIgnoreCase))
                return _lastGeo;

            string url = "https://geocoding-api.open-meteo.com/v1/search?name="
                         + Uri.EscapeDataString(city) + "&count=1&language=zh&format=json";
            string json = await _http.GetStringAsync(url).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                    return null;

                var r = results[0];
                double lat = r.GetProperty("latitude").GetDouble();
                double lon = r.GetProperty("longitude").GetDouble();
                string name = r.TryGetProperty("name", out var n) ? n.GetString() : city;
                string region = r.TryGetProperty("admin1", out var a1) ? a1.GetString() : (r.TryGetProperty("country", out var c) ? c.GetString() : "");

                var result = (lat, lon, name, region);
                _lastGeoQuery = city;
                _lastGeo = result;
                return result;
            }
        }

        private static WeatherInfo Parse(string json, string city, string region)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var info = new WeatherInfo
                {
                    City = city,
                    Region = region,
                    UpdatedAt = DateTime.Now
                };

                if (root.TryGetProperty("current", out var cur))
                {
                    info.Temp = GetDouble(cur, "temperature_2m");
                    info.Feels = GetDouble(cur, "apparent_temperature");
                    info.Humidity = (int)Math.Round(GetDouble(cur, "relative_humidity_2m"));
                    info.Wind = GetDouble(cur, "wind_speed_10m");
                    info.Code = (int)Math.Round(GetDouble(cur, "weather_code"));
                    info.IsDay = cur.TryGetProperty("is_day", out var d) ? d.GetInt32() == 1 : true;
                }

                if (root.TryGetProperty("daily", out var daily))
                {
                    var times = daily.GetProperty("time");
                    var codes = daily.GetProperty("weather_code");
                    var maxs = daily.GetProperty("temperature_2m_max");
                    var mins = daily.GetProperty("temperature_2m_min");
                    JsonElement? prec = daily.TryGetProperty("precipitation_probability_max", out var pp) ? pp : (JsonElement?)null;

                    int n = Math.Min(5, times.GetArrayLength());
                    for (int i = 0; i < n; i++)
                    {
                        var item = new DailyWeather
                        {
                            Code = codes[i].ValueKind == JsonValueKind.Number ? codes[i].GetInt32() : 0,
                            Max = maxs[i].ValueKind == JsonValueKind.Number ? maxs[i].GetDouble() : 0,
                            Min = mins[i].ValueKind == JsonValueKind.Number ? mins[i].GetDouble() : 0,
                            Label = DayLabel(i, times[i].GetString())
                        };

                        if (prec.HasValue && prec.Value.GetArrayLength() > i && prec.Value[i].ValueKind == JsonValueKind.Number)
                            item.PrecipProb = prec.Value[i].GetInt32();

                        info.Daily.Add(item);
                    }

                    if (info.Daily.Count > 0)
                    {
                        info.High = info.Daily[0].Max;
                        info.Low = info.Daily[0].Min;
                    }
                }

                return info;
            }
        }

        private static string DayLabel(int index, string isoDate)
        {
            if (index == 0) return "今天";
            if (DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                var zh = new CultureInfo("zh-CN");
                return dt.ToString("ddd", zh);
            }

            return isoDate;
        }

        private static double GetDouble(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var v)) return 0;
            return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        }

        private static void SaveCache(WeatherInfo info)
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.DataDir);
                System.IO.File.WriteAllText(CachePath, JsonSerializer.Serialize(info));
            }
            catch
            {
                // 缓存失败忽略
            }
        }

        public static WeatherInfo LoadCache()
        {
            try
            {
                if (!System.IO.File.Exists(CachePath)) return null;
                var info = JsonSerializer.Deserialize<WeatherInfo>(System.IO.File.ReadAllText(CachePath));
                return info;
            }
            catch
            {
                return null;
            }
        }
    }
}
