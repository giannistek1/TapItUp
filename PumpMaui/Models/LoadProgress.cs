namespace PumpMaui.Models;

public class LoadProgress
{
    public string Message { get; set; } = "";
    public int Current { get; set; }
    public int Total { get; set; }

    public double Percentage =>
        Total == 0 ? 0 : (double)Current / Total;
}
