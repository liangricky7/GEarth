namespace GEarth.Core;

// School: a citizen pays a fixed tuition and studies, full time for four weeks (an employee leaves their job)
// or, keeping their job, part time at half hours and half pay for eight weeks. Graduates produce a quarter
// more, are hired first and are paid 15% more. Tuition pays the teachers; a student who cannot pay borrows it
// from the bank (the government guarantees the loan); the government can also pay part or all of it, a
// stipend while studying, and a bonus to employers.
public static class SocietySchool
{
    // The cheapest school with a free seat.
    public static Agent? OpenSchool(World world, string country) =>
        SocietyLabor.Firms(world, country, "school")
            .Where(school => SocietyLabor.Students(world, school).Length < SocietyLabor.Seats(world, school))
            .OrderBy(school => school.PriceMinor).ThenBy(school => school.Id).FirstOrDefault();

    // The student's own share of tuition after the government's subsidy.
    public static long OwnShare(Nation nation, Agent school) => (long)school.PriceMinor * (100 - nation.TuitionSubsidyPct) / 100;

    // The weekly pay a raise is measured against: the current salary, or the trade's usual pay when out of work.
    private static long Pay(Agent person) => person.EmployerId is not null ? person.SalaryMinor
        : SocietyLabor.TableWage.GetValueOrDefault(person.Occupation, 1000);

    public static long Raise(Agent person) => Pay(person) * SocietyLabor.GraduatePayPremiumPct / 100;

    // What enrolling costs and returns, or null when the citizen cannot enroll this way. The figures are shown
    // to the citizen with the option; a loan covers the tuition they cannot pay themselves.
    public static object? Offer(World world, Agent person, bool partTime)
    {
        if (person.Role != "citizen" || SocietyLabor.IsStudent(person) || person.Graduated
            || partTime && person.EmployerId is null || OpenSchool(world, person.CountryId) is not { } school)
            return null;
        var nation = world.Society!.Nations[person.CountryId];
        var own = OwnShare(nation, school);
        if (world.Agents[nation.Id + "_government"].CashMinor < school.PriceMinor - own)
            return null;
        var loan = SocietyBank.Liquid(world, person) >= own ? 0
            : SocietyBank.LoanOf(world, person.Id) is null && SocietyBank.CanLend(world, SocietyBank.BankOf(world, person.CountryId), own) ? own : -1;
        if (loan < 0)
            return null;
        // Full time gives up four weeks of pay (or of benefit without a stipend); part time gives up half pay for eight.
        var givenUp = person.EmployerId is not null ? 4 * person.SalaryMinor : nation.StudentStipend ? 0 : 4L * nation.UnemploymentBenefitMinor;
        var raise = Raise(person);
        var offer = new Dictionary<string, object>
        {
            ["part_time"] = partTime, ["leaves_job"] = person.EmployerId is not null && !partTime,
            ["course_days"] = partTime ? SocietyLabor.PartTimeCourseDays : SocietyLabor.CourseDays,
            ["tuition_minor"] = own, ["pay_given_up_minor"] = givenUp, ["raise_minor_per_week"] = raise
        };
        if (loan > 0)
            offer["student_loan_minor"] = loan;
        if (raise > 0)
            offer["payback_weeks"] = (own + givenUp + raise - 1) / raise;
        return offer;
    }

    public static void Enroll(World world, Agent person, bool partTime, string cause)
    {
        var nation = world.Society!.Nations[person.CountryId];
        var school = OpenSchool(world, person.CountryId)!;
        var government = world.Agents[nation.Id + "_government"];
        var own = OwnShare(nation, school);
        var loan = SocietyBank.Liquid(world, person) >= own ? null : SocietyBank.StudentLoan(world, person, own);
        SocietyBank.Draw(world, person, own);
        SocietyEconomy.Sell(world, person, school, own, false, 1, "tuition");
        SocietyEconomy.Sell(world, government, school, school.PriceMinor - own, kind: "tuition_subsidy");
        var quit = partTime ? null : person.EmployerId;
        if (!partTime)
        {
            person.EmployerId = null; // full-time study: an employee leaves their job to enroll
            person.SalaryMinor = 0;
            person.OvertimeDays = 0;
            person.UnemployedSinceTick = world.Tick;
        }
        person.PartTimeStudent = partTime;
        person.StudentUntilTick = world.Tick + (partTime ? SocietyLabor.PartTimeCourseDays : SocietyLabor.CourseDays);
        person.SchoolId = school.Id;
        world.Emit("enrolled", "rule", [person.Id, school.Id, nation.Id], new
        {
            tuition_minor = school.PriceMinor, paid_by_student_minor = own, student_loan = loan?.Id, part_time = partTime,
            quit_employer = quit, graduates_tick = person.StudentUntilTick
        }, cause);
    }

    // A graduate who kept their job gets the graduate premium from their employer at once.
    public static void Graduate(World world, Agent person)
    {
        person.Graduated = true;
        person.StudentUntilTick = -1;
        person.SchoolId = null;
        person.PartTimeStudent = false;
        if (person.EmployerId is null)
            person.UnemployedSinceTick = world.Tick;
        else
            person.SalaryMinor += Raise(person);
        world.Emit("graduated", "rule", [person.Id, person.CountryId], new
        {
            output_boost = SocietyLabor.GraduateOutputBoost, pay_premium_pct = SocietyLabor.GraduatePayPremiumPct,
            occupation = person.Occupation, employed = person.EmployerId is not null
        });
    }
}
