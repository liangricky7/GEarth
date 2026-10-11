using GEarth.Core;
using System.Text.Json;

void Show(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
void Check(bool condition, string name) { if (!condition) throw new Exception("Failed review assertion: " + name); }
World Fresh(string id) => SocietyFactory.Create(id, false, 1100);

// Invoke the actual historical weekly implementation against copied current economy code.
var multiple = Fresh("multi");
foreach (var person in SocietyWar.Soldiers(multiple, multiple.Society!.Nations["aster"])) SocietyLabor.Fire(multiple, person);
foreach (var other in new[] { "beryl", "cinder" }) SocietyDiplomacy.Relation(multiple, "aster", other).AtWar = true;
multiple.Society.Nations["aster"].NoFitSoldierWeeks = 1;
multiple.Tick = 14;
SocietyWar.Day(multiple);
Check(!SocietyDiplomacy.Relation(multiple,"aster","beryl").AtWar && SocietyDiplomacy.Relation(multiple,"aster","cinder").AtWar && multiple.Society.Nations["aster"].NoFitSoldierWeeks == 0, "ending one war resets exhaustion for another");
Show(new { probe = "multiwar_surrender", first_war = SocietyDiplomacy.Relation(multiple,"aster","beryl").AtWar, second_war = SocietyDiplomacy.Relation(multiple,"aster","cinder").AtWar, multiple.Society.Nations["aster"].NoFitSoldierWeeks, ended = multiple.Events.Where(e=>e.EventType=="war_ended").Select(e=>e.EntityIds).ToArray() });

var missile = Fresh("missile");
var empty = missile.Society!.Nations["aster"];
foreach (var person in SocietyWar.Soldiers(missile, empty)) SocietyLabor.Fire(missile, person);
empty.Missiles = 1;
SocietyDiplomacy.Relation(missile, "aster", "beryl").AtWar = true;
foreach (var tick in new[] {7,14,21}) { missile.Tick=tick; SocietyWar.Day(missile); }
Check(SocietyWar.Soldiers(missile,empty).All(SocietyEconomy.TooSick) && empty.Missiles==1 && empty.NoFitSoldierWeeks==0 && SocietyDiplomacy.Relation(missile,"aster","beryl").AtWar,"stockpiled missile prevents soldier-based surrender");
Show(new { probe="missile_prevents_surrender", fit_soldiers=SocietyWar.Soldiers(missile,empty).Count(a=>!SocietyEconomy.TooSick(a)), empty.Missiles, empty.NoFitSoldierWeeks, at_war=SocietyDiplomacy.Relation(missile,"aster","beryl").AtWar });

var embargo = Fresh("embargo");
SocietyDiplomacy.Relation(embargo,"cinder","dune").Score = -100;
var buyer = SocietyLabor.Firms(embargo,"cinder","well").First();
var seller = SocietyLabor.Firms(embargo,"dune","well").First();
var path=SocietyTrade.Path(embargo,seller.LocationId,buyer.LocationId,"cheapest");
Check(path != null && path.EdgeIds.Length==2 && SocietyTrade.CanImport(embargo,buyer,seller,"fuel",1,"cheapest"),"route closure permits direct counterparty import via third country");
Show(new { probe="embargo_route_bypass", path=path?.EdgeIds, can_import=SocietyTrade.CanImport(embargo,buyer,seller,"fuel",1,"cheapest"), buyer_country=buyer.CountryId, seller_country=seller.CountryId });

var gifts = Fresh("gifts");
var donor=gifts.Society!.Nations["aster"];var recipient=gifts.Society.Nations["beryl"];
var holders=gifts.InCountry("aster").Where(a=>a.Role=="business" && a.Inventory.GetValueOrDefault("ore")>0).ToArray();
// Set an accounting-conserved distributed stock to expose per-holder rounding.
foreach(var h in holders){gifts.Society.LostGoods["ore"]+=h.Inventory["ore"]-11;h.Inventory["ore"]=11;}
donor.Stocks["ore"]=holders.Sum(h=>h.Inventory["ore"]);
var before=gifts.InCountry("beryl").Sum(a=>a.Inventory.GetValueOrDefault("ore"));
var sourceBefore=holders.ToDictionary(h=>h.Id,h=>h.Inventory["ore"]);
var policy=new PolicyProposal{ActionType="send_gift",Params=Wire.Node(new{country_id="beryl",kind="ore",amount=10}).AsObject()};
SocietyWar.Enact(gifts,donor,policy);
var delivered=gifts.InCountry("beryl").Sum(a=>a.Inventory.GetValueOrDefault("ore"))-before;
SocietyEconomy.AssertConservation(gifts);
Check(delivered==9 && gifts.Events.Last(e=>e.EventType=="gift_sent").Payload["amount"]!.GetValue<int>()==10,"raw gift moves fewer units than reported");
Show(new { probe="gift_rounding", requested=10, delivered, reported=gifts.Events.Last(e=>e.EventType=="gift_sent").Payload["amount"]!.GetValue<int>(), sources=holders.Select(h=>new{h.Id,h.OwnerId,removed=sourceBefore[h.Id]-h.Inventory["ore"]}).ToArray() });

var strike=Fresh("strike");var striker=SocietyWar.Soldiers(strike,strike.Society!.Nations["aster"])[0];
var strength=SocietyWar.Strength(strike,strike.Society.Nations["aster"]);striker.WorkEffort=0;
Check(SocietyWar.Strength(strike,strike.Society.Nations["aster"])==strength && SocietyLabor.DayEffort(strike,striker)==0,"voluntary striker still counts toward military strength");
Show(new {probe="persistent_voluntary_strike", before=strength, after=SocietyWar.Strength(strike,strike.Society.Nations["aster"]), effort=SocietyLabor.DayEffort(strike,striker)});
