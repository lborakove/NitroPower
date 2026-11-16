namespace NitroPower.Core.Policy;

public readonly record struct FanCurvePoint(double TempC, int Percent);

/// <summary>
/// Temperature → fan duty curve with linear interpolation between points, used
/// when the firmware has no fan table of its own. Duty is kept within 1..100,
/// because 0 would hand the fan back to the firmware's automatic curve.
/// </summary>
public sealed class FanCurve
{
    public const int MinPercent = 1;
    public const int MaxPercent = 100;

    public IReadOnlyList<FanCurvePoint> Points { get; }

    public FanCurve(IEnumerable<FanCurvePoint> points)
    {
        var sorted = points.OrderBy(p => p.TempC).ToList();
        if (sorted.Count < 2)
            throw new ArgumentException("a fan curve needs at least 2 points", nameof(points));
        for (var i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            if (p.Percent is < MinPercent or > MaxPercent)
                throw new ArgumentException($"duty {p.Percent}% at {p.TempC}°C is outside {MinPercent}..{MaxPercent}", nameof(points));
            if (p.TempC is < 0 or > 110)
                throw new ArgumentException($"temperature {p.TempC}°C is outside 0..110", nameof(points));
            if (i > 0 && p.TempC == sorted[i - 1].TempC)
                throw new ArgumentException($"two points at {p.TempC}°C", nameof(points));
            if (i > 0 && p.Percent < sorted[i - 1].Percent)
                throw new ArgumentException("fan duty must not decrease as temperature rises", nameof(points));
        }
        Points = sorted;
    }

    public int Evaluate(double tempC)
    {
        if (tempC <= Points[0].TempC)
            return Points[0].Percent;
        if (tempC >= Points[^1].TempC)
            return Points[^1].Percent;
        for (var i = 1; i < Points.Count; i++)
        {
            var (a, b) = (Points[i - 1], Points[i]);
            if (tempC > b.TempC)
                continue;
            var t = (tempC - a.TempC) / (b.TempC - a.TempC);
            return (int)Math.Round(a.Percent + t * (b.Percent - a.Percent));
        }
        return Points[^1].Percent;
    }

    /// <summary>A quiet-but-safe default for a 15" gaming laptop.</summary>
    public static FanCurve Default { get; } = new([
        new(40, 20), new(55, 30), new(65, 45), new(75, 65), new(85, 85), new(92, 100),
    ]);
}

/// <summary>
/// Applies a curve with hysteresis: the duty follows rising temperatures at once
/// but only drops once the temperature has fallen <see cref="HysteresisC"/>
/// below the point that raised it, so the fan does not hunt.
/// </summary>
public sealed class FanCurveController(FanCurve curve, double hysteresisC = 3.0)
{
    private int? _current;
    private double _raisedAt;

    public double HysteresisC => hysteresisC;
    public FanCurve Curve => curve;

    public int Next(double tempC)
    {
        var target = curve.Evaluate(tempC);
        if (_current is null || target > _current)
        {
            _current = target;
            _raisedAt = tempC;
        }
        else if (target < _current && tempC <= _raisedAt - hysteresisC)
        {
            _current = target;
            _raisedAt = tempC;
        }
        return _current.Value;
    }
}
