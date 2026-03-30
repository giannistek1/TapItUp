namespace PumpMaui.Extensions;

public static class DictionaryExtensions
{
    public static string Get(this Dictionary<string, string> dict, string key, string defaultValue = "")
    {
        return dict.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;
    }

    public static double GetDouble(this Dictionary<string, string> dict, string key, double defaultValue = 0)
    {
        if (dict.TryGetValue(key, out var value) && double.TryParse(value, out var result))
            return result;

        return defaultValue;
    }
}
