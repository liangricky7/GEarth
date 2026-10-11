using System.IO.Compression;
using System.Text.Json;

namespace GEarth.Core;

// Verbose runs write a gzipped NDJSON history for the renderer: a header line, one "day" line per tick
// with every agent's and country's state, the money that moved and the events since the previous line,
// and an "end" line. Rows are arrays whose columns the header names. Cash is in minor units.
public sealed class HistoryWriter : IDisposable
{
    public static readonly string[] AgentFields =
        ["id", "role", "country", "location", "occupation", "employer", "cash", "health", "wellbeing",
         "specialty", "price", "stock", "staff"];
    public static readonly string[] NationFields =
        ["id", "government", "residents", "unemployed", "approval", "government_cash", "gini",
         "unmet_needs", "wars", "stock"];
    public static readonly string[] FlowFields = ["from", "to", "kind", "amount", "count"];
    public static readonly string[] ShipmentFields =
        ["id", "good", "quantity", "buyer", "seller", "edge", "edge_remaining_ticks"];

    private readonly StreamWriter writer;
    private long lastSeq;

    public HistoryWriter(string path, World world)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(new GZipStream(file, CompressionLevel.Fastest));
        world.Flows = new();
        Line(new
        {
            type = "header",
            format = "gearth.history.v1",
            run_id = world.Id,
            world.Seed,
            world.Mode,
            world.InitialCashMinor,
            agent_fields = AgentFields,
            nation_fields = NationFields,
            flow_fields = FlowFields,
            shipment_fields = ShipmentFields,
            world.Countries,
            world.Locations,
            routes = world.Routes.Values,
            government_profiles = Governments.Profiles
        });
    }

    public static string PathFor(string runId)
    {
        if (runId.Length is 0 or > 100 || runId.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('-' or '_')))
            throw new ArgumentException("Invalid run ID");
        return Path.GetFullPath(Path.Combine(".data/history", runId + ".ndjson.gz"));
    }

    // State at the end of the current tick plus everything that happened since the previous line.
    public void Day(World world) => Line(Frame(world, "day"));

    public void End(World world) => Line(Frame(world, "end"));

    // Sync-flushes the gzip stream so every line written so far can be read while the run continues.
    public void Flush() => writer.Flush();

    private object Frame(World world, string type)
    {
        var society = world.Society!;
        var staff = world.Agents.Values.Where(agent => agent.EmployerId is not null && SocietyLabor.IsResident(agent))
            .CountBy(agent => agent.EmployerId!).ToDictionary();
        var events = world.Events.Where(item => item.Seq > lastSeq).ToArray();
        lastSeq = world.Seq;
        var moved = world.Flows!;
        var flows = moved.Select(flow => new object[]
            { flow.Key.From, flow.Key.To, flow.Key.Kind, flow.Value.Amount, flow.Value.Count }).ToArray();
        moved.Clear();
        return new
        {
            type,
            tick = world.Tick,
            status = world.Status,
            agents = world.Agents.Values.Select(agent =>
            {
                var firm = agent.Role is "business" or "defunct";
                return new object?[]
                {
                    agent.Id, agent.Role, agent.CountryId, agent.LocationId, agent.Occupation, agent.EmployerId,
                    agent.CashMinor, agent.Health, agent.Wellbeing, agent.Specialty,
                    firm ? agent.PriceMinor : null,
                    firm && agent.Specialty is { } good && agent.Inventory.TryGetValue(good, out var stock) ? stock : null,
                    staff.GetValueOrDefault(agent.Id)
                };
            }),
            nations = society.Nations.Values.Select(nation =>
            {
                var people = world.InCountry(nation.Id);
                return new object[]
                {
                    nation.Id, nation.GovernmentId, people.Count(SocietyLabor.IsResident), nation.Unemployed,
                    nation.Approval, world.Agents[nation.Id + "_government"].CashMinor, nation.GiniCash,
                    nation.UnmetNeedsLastCycle,
                    society.Relations.Values.Count(relation => relation.AtWar
                        && (relation.First == nation.Id || relation.Second == nation.Id)),
                    SocietyFactory.Goods.ToDictionary(good => good, good => people.Sum(agent => (long)agent.Inventory.GetValueOrDefault(good)))
                };
            }),
            shipments = society.Cargo.Where(cargo => cargo.Status == "in_transit").Select(cargo => new object?[]
            {
                cargo.Id, cargo.Good, cargo.Quantity, cargo.BuyerId, cargo.SellerId,
                cargo.Plan?.EdgeIds.ElementAtOrDefault(cargo.EdgeIndex), cargo.EdgeRemainingTicks
            }),
            flows,
            events
        };
    }

    private void Line(object value)
    {
        writer.Write(JsonSerializer.Serialize(value, Wire.Json));
        writer.Write('\n');
    }

    public void Dispose() => writer.Dispose();
}
