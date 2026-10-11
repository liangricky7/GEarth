namespace Faultline.Core;

public sealed class AgentWeights
{
    public double Wealth { get; set; } = 1;
    public double Productivity { get; set; } = 1;
    public double BargainingPower { get; set; } = 1;
    public Dictionary<string, double> Preferences { get; set; } = new()
    {
        ["security"] = 0.2, ["wealth"] = 0.2, ["health"] = 0.2,
        ["belonging"] = 0.2, ["mobility"] = 0.2
    };
}

public static class AgentEndowments
{
    public const string Version = "endowments_v1";

    public static AgentWeights Create(int seed, string identity)
    {
        double Sample(string dimension) => SocietyFactory.Sample(seed, identity, Version + ":" + dimension);
        var normal = Math.Sqrt(-2 * Math.Log(Math.Max(1.0 / 256, Sample("wealth_radius"))))
            * Math.Cos(2 * Math.PI * Sample("wealth_angle"));
        var preferences = new[] { "security", "wealth", "health", "belonging", "mobility" }
            .ToDictionary(name => name, name => 0.1 + Sample("preference:" + name));
        var total = preferences.Values.Sum();
        return new AgentWeights
        {
            Wealth = Math.Clamp(Math.Exp(normal * 0.8), 0.25, 4),
            Productivity = 0.6 + 0.8 * Sample("productivity"),
            BargainingPower = 0.75 + 0.5 * Sample("bargaining"),
            Preferences = preferences.ToDictionary(pair => pair.Key, pair => pair.Value / total)
        };
    }

    public static void AllocateCash(Agent[] citizens, long perResidentMinor = 2000, long? budgetMinor = null)
    {
        if (citizens.Length == 0)
            return;
        var budget = budgetMinor ?? perResidentMinor * citizens.Length;
        var totalWeight = citizens.Sum(citizen => citizen.Weights.Wealth);
        foreach (var citizen in citizens)
            citizen.CashMinor = (long)Math.Floor(budget * citizen.Weights.Wealth / totalWeight);
        var remainder = budget - citizens.Sum(citizen => citizen.CashMinor);
        foreach (var citizen in citizens.Take((int)remainder))
            citizen.CashMinor++;
    }
}
