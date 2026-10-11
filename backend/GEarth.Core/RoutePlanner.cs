namespace GEarth.Core;

internal static class RoutePlanner
{
    private const string Origin = "aster_farm";
    private const string Destination = "beryl_hospital";

    public static PathPlan? FindPath(World world, string objective)
        => FindPath(world, objective, Origin, Destination);

    public static PathPlan? FindPath(World world, string objective, string origin, string destination,
        Func<Route, bool>? permitted = null)
    {
        if (objective is not ("fastest" or "cheapest" or "cautious"))
            return null;

        var queue = new PriorityQueue<string, double>();
        var distances = new Dictionary<string, double> { [origin] = 0 };
        var previousEdges = new Dictionary<string, Route>();
        queue.Enqueue(origin, 0);

        while (queue.TryDequeue(out var location, out var distance))
        {
            if (distance > distances[location])
                continue;
            if (location == destination)
                break;

            foreach (var route in world.Routes.Values.Where(route => route.Open && route.From == location
                && (permitted is null || permitted(route))))
            {
                var nextDistance = distance + Weight(route, objective);
                if (distances.TryGetValue(route.To, out var existingDistance) && existingDistance <= nextDistance)
                    continue;

                distances[route.To] = nextDistance;
                previousEdges[route.To] = route;
                queue.Enqueue(route.To, nextDistance);
            }
        }

        if (!distances.ContainsKey(destination))
            return null;

        var edges = new List<Route>();
        for (var location = destination; location != origin; location = previousEdges[location].From)
            edges.Add(previousEdges[location]);

        edges.Reverse();

        return new PathPlan(
            edges.Select(route => route.Id).ToArray(),
            edges.Sum(route => route.TravelTicks),
            edges.Sum(route => route.CostMinor),
            objective);
    }

    private static double Weight(Route route, string objective)
    {
        return objective switch
        {
            "cheapest" => route.CostMinor,
            "cautious" => route.TravelTicks + 10 * route.RiskScore,
            _ => route.TravelTicks
        };
    }
}
