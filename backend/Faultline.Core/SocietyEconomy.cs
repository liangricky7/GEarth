namespace Faultline.Core;

public static class SocietyEconomy
{
    public static void Cycle(World world)
    {
        var society = world.Society!;
        var byCountry = world.Agents.Values.GroupBy(agent => agent.CountryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var nation in society.Nations.Values)
        {
            var agents = byCountry[nation.Id];
            var citizens = agents.Where(agent => agent.Role == "citizen").ToArray();
            var government = world.Agents[nation.Id + "_government"];
            var businesses = agents.Where(agent => agent.Role == "business").ToArray();
            var ownership = Governments.Get(nation.GovernmentId).PublicOwnershipPercent;
            var wagesBefore = nation.WagesPaidMinor;
            nation.UnmetNeedsLastCycle = 0;
            nation.FulfilledNeedsLastCycle = 0;

            foreach (var business in businesses)
            {
                var reserve = citizens.Length * 200L;
                if (ownership > 0 && business.CashMinor > reserve)
                    Transfer(business, government, (business.CashMinor - reserve) * ownership / 1000);
                if (ownership > 0 && business.CashMinor < citizens.Length * 40L)
                    Transfer(government, business, Math.Min(government.CashMinor / 10, citizens.Length * 140L));
            }

            foreach (var citizen in citizens)
            {
                var employer = world.Agents[citizen.EmployerId!];
                var wage = Math.Min(employer.CashMinor, citizen.WorkEffort == 0 ? 0 : 140L * citizen.WorkEffort);
                Transfer(employer, citizen, wage);
                var tax = wage * nation.TaxBasisPoints / 10000;
                Transfer(citizen, government, tax);
                nation.TaxesCollectedMinor += tax;
                nation.WagesPaidMinor += wage;
            }

            var welfareBudget = Math.Min(government.CashMinor, (long)nation.WelfareMinor * citizens.Length);
            var perPerson = citizens.Length == 0 ? 0 : welfareBudget / citizens.Length;
            foreach (var citizen in citizens)
            {
                Transfer(government, citizen, perPerson);
                nation.WelfarePaidMinor += perPerson;
            }

            foreach (var business in businesses.OrderBy(business => business.Specialty == "energy" ? 0 : 1))
                Produce(world, nation, business, citizens.Where(citizen => citizen.EmployerId == business.Id).ToArray());

            var medicine = businesses.Single(business => business.Specialty == "medicine");
            var serviceBudget = Math.Min(government.CashMinor, (long)nation.ServiceBudgetPerResidentMinor * citizens.Length);
            var doses = (int)Math.Min(medicine.Inventory["medicine"], serviceBudget / medicine.PriceMinor);
            SocietyEconomy.Transfer(government, medicine, (long)doses * medicine.PriceMinor);
            medicine.Inventory["medicine"] -= doses;
            society.ConsumedGoods["medicine"] += doses;

            var start = citizens.Length == 0 ? 0 : world.Tick / 5 % citizens.Length;
            for (var index = 0; index < citizens.Length; index++)
            {
                var citizen = citizens[(index + start) % citizens.Length];
                if (citizen.WorkEffort == 2)
                    citizen.Wellbeing = Math.Max(0, citizen.Wellbeing - 5);
                if (index < doses * 10)
                    citizen.Wellbeing = Math.Min(100, citizen.Wellbeing + 2);
                foreach (var good in new[] { "food", "energy" })
                {
                    var seller = businesses.Single(business => business.Specialty == good);
                    var missing = Math.Max(0, citizen.TargetStock - citizen.Inventory[good]);
                    var quantity = (int)Math.Min(Math.Min(missing, seller.Inventory[good]), citizen.CashMinor / seller.PriceMinor);
                    if (quantity > 0)
                    {
                        Transfer(citizen, seller, (long)quantity * seller.PriceMinor);
                        seller.Inventory[good] -= quantity;
                        citizen.Inventory[good] += quantity;
                    }
                    if (citizen.Inventory[good] > 0)
                    {
                        citizen.Inventory[good]--;
                        society.ConsumedGoods[good]++;
                        nation.FulfilledNeedsLastCycle++;
                        citizen.Wellbeing = Math.Min(100, citizen.Wellbeing + 1);
                    }
                    else
                    {
                        nation.UnmetNeedsLastCycle++;
                        citizen.Wellbeing = Math.Max(0, citizen.Wellbeing - 5);
                    }
                }
            }
            nation.UnmetNeeds += nation.UnmetNeedsLastCycle;
            world.UnmetNeeds += nation.UnmetNeedsLastCycle;
            var tensions = society.Relations.Values.Where(relation => relation.AtWar
                && (relation.First == nation.Id || relation.Second == nation.Id)).ToArray();
            if (tensions.Length > 0)
                ConflictCosts(world, nation, government, businesses, tensions.Length);

            world.Emit("economy_cycle", "rule", [nation.Id], new
            {
                nation.UnmetNeedsLastCycle, nation.FulfilledNeedsLastCycle,
                wages_paid_minor = nation.WagesPaidMinor - wagesBefore,
                funded_welfare_per_person_minor = perPerson,
                production_units = nation.ProducedUnits,
                routine_household_purchases = true
            });
        }
        SettleCredit(world);
        UpdateMetrics(world);
    }

    private static void Produce(World world, Nation nation, Agent producer, Agent[] workers)
    {
        var good = producer.Specialty!;
        var multiplier = good switch
        {
            "food" => nation.FoodMultiplier,
            "energy" => nation.EnergyMultiplier,
            _ => 1
        };
        if (nation.CrisisUntilTick > world.Tick && nation.CrisisGood == good)
            multiplier *= 0.35;
        var potential = (int)(workers.Sum(worker => worker.WorkEffort * worker.Wellbeing / 100.0)
            * (good is "food" or "energy" ? 12 : 4) * multiplier * nation.Productivity);
        var stockLimit = Math.Max(200, nation.Population * (good is "food" or "energy" ? 20 : 6));
        var quantity = Math.Min(potential, Math.Max(0, stockLimit - producer.Inventory[good]));
        var energyNeeded = good == "energy" ? 0 : (quantity + 9) / 10;
        if (energyNeeded > 0)
        {
            var seller = world.Agents[nation.Id + "_energy"];
            var required = Math.Max(0, energyNeeded - producer.Inventory["energy"]);
            var purchased = (int)Math.Min(Math.Min(required, seller.Inventory["energy"]), producer.CashMinor / seller.PriceMinor);
            Transfer(producer, seller, (long)purchased * seller.PriceMinor);
            seller.Inventory["energy"] -= purchased;
            producer.Inventory["energy"] += purchased;
            quantity = Math.Min(quantity, producer.Inventory["energy"] * 10);
            energyNeeded = (quantity + 9) / 10;
            producer.Inventory["energy"] -= energyNeeded;
            world.Society!.ConsumedGoods["energy"] += energyNeeded;
        }
        if (good == "medicine")
        {
            var seller = world.Agents[nation.Id + "_tools"];
            var required = Math.Max(0, (quantity + 9) / 10 - producer.Inventory["tools"]);
            var purchased = (int)Math.Min(Math.Min(required, seller.Inventory["tools"]), producer.CashMinor / seller.PriceMinor);
            Transfer(producer, seller, (long)purchased * seller.PriceMinor);
            seller.Inventory["tools"] -= purchased;
            producer.Inventory["tools"] += purchased;
            quantity = Math.Min(quantity, producer.Inventory["tools"] * 10);
            var toolsNeeded = (quantity + 9) / 10;
            producer.Inventory["tools"] -= toolsNeeded;
            world.Society!.ConsumedGoods["tools"] += toolsNeeded;
        }
        producer.Inventory[good] += quantity;
        nation.ProducedUnits += quantity;
        world.Society!.ProducedGoods[good] += quantity;
    }

    private static void ConflictCosts(World world, Nation nation, Agent government, Agent[] businesses, int conflicts)
    {
        var bank = world.Agents[nation.Id + "_bank"];
        Transfer(government, bank, Math.Min(government.CashMinor, 1000L * conflicts));
        foreach (var business in businesses)
        {
            var good = business.Specialty!;
            var destroyed = Math.Min(business.Inventory[good], Math.Max(1, nation.Population / 40) * conflicts);
            business.Inventory[good] -= destroyed;
            nation.LostUnits += destroyed;
            world.Society!.LostGoods[good] += destroyed;
        }
        world.Emit("conflict_costs", "rule", [nation.Id], new { conflicts, nation.LostUnits });
    }

    private static void SettleCredit(World world)
    {
        foreach (var credit in world.Society!.Credit.Where(credit => credit.Status == "active" && credit.DueTick <= world.Tick))
        {
            var borrower = world.Agents[credit.BorrowerId];
            var bank = world.Agents[credit.BankId];
            var paid = Math.Min(borrower.CashMinor, credit.DueMinor);
            Transfer(borrower, bank, paid);
            credit.Status = paid == credit.DueMinor ? "repaid" : "defaulted";
            if (credit.Status == "defaulted")
                world.Society.Nations[borrower.CountryId].DefaultedLoans++;
            world.Emit("loan_settled", "rule", [credit.Id, borrower.Id, bank.Id], new
            {
                credit.Status, paid_minor = paid, loss_minor = credit.DueMinor - paid,
                settlement = "simulated internal ledger"
            });
        }
    }

    public static void UpdateMetrics(World world)
    {
        var groups = world.Agents.Values.GroupBy(agent => agent.CountryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var nation in world.Society!.Nations.Values)
        {
            var agents = groups[nation.Id];
            var citizens = agents.Where(agent => agent.Role == "citizen").ToArray();
            var support = citizens.Length == 0 ? 0 : citizens.Count(citizen => citizen.SupportsLeadership) / (double)citizens.Length;
            var wellbeing = citizens.Length == 0 ? 0 : citizens.Average(citizen => citizen.Wellbeing) / 100;
            nation.Approval = Math.Round(support * 0.5 + wellbeing * 0.5, 3);
            nation.GiniCash = Math.Round(Gini(citizens.Select(citizen => citizen.CashMinor).ToArray()), 4);
            nation.Stocks = SocietyFactory.Goods.ToDictionary(good => good,
                good => agents.Where(agent => agent.Role == "business").Sum(agent => agent.Inventory[good]));
            var totalNeeds = nation.FulfilledNeedsLastCycle + nation.UnmetNeedsLastCycle;
            nation.History.Add(new CountryMetric(world.Tick,
                totalNeeds == 0 ? 1 : nation.FulfilledNeedsLastCycle / (double)totalNeeds,
                nation.Approval, nation.GiniCash, world.Agents[nation.Id + "_government"].CashMinor,
                nation.TradeValueMinor, nation.ProducedUnits));
            if (nation.History.Count > 100)
                nation.History.RemoveAt(0);
        }
    }

    public static double Gini(long[] balances)
    {
        if (balances.Length == 0 || balances.Sum() == 0)
            return 0;
        var ordered = balances.Order().ToArray();
        var weighted = ordered.Select((balance, index) => (2.0 * (index + 1) - ordered.Length - 1) * balance).Sum();
        return weighted / (ordered.Length * (double)ordered.Sum());
    }

    public static void Transfer(Agent from, Agent to, long amount)
    {
        if (amount < 0 || amount > from.CashMinor)
            throw new InvalidOperationException("Unfunded transfer");
        from.CashMinor -= amount;
        to.CashMinor += amount;
    }

    public static void AssertConservation(World world)
    {
        var society = world.Society!;
        var money = world.Agents.Values.Sum(agent => agent.CashMinor) + society.Cargo.Sum(cargo => cargo.EscrowMinor);
        if (money != world.InitialCashMinor || world.Agents.Values.Any(agent => agent.CashMinor < 0
            || agent.Inventory.Values.Any(quantity => quantity < 0)))
            throw new InvalidOperationException("Cash or inventory invariant failed");
        foreach (var good in SocietyFactory.Goods)
        {
            var physical = world.Agents.Values.Sum(agent => (long)agent.Inventory[good])
                + society.Cargo.Where(cargo => cargo.Status == "in_transit" && cargo.Good == good).Sum(cargo => (long)cargo.Quantity);
            if (physical != society.InitialGoods[good] + society.ProducedGoods[good]
                - society.ConsumedGoods[good] - society.LostGoods[good])
                throw new InvalidOperationException("Goods conservation failed");
        }
    }
}
