namespace GEarth.Core;

// School: a resident who is not working (anyone employed must quit to enroll) pays a fixed tuition, studies
// for four weeks, and graduates producing a quarter more, hired first at the same pay. Tuition pays the
// teachers; the government can pay part or all of it, a stipend while studying, and a bonus to employers.
public static class SocietySchool
{
    // The cheapest school with a free seat.
    public static Agent? OpenSchool(World world, string country) =>
        SocietyLabor.Firms(world, country, "school")
            .Where(school => SocietyLabor.Students(world, school).Length < SocietyLabor.Seats(world, school))
            .OrderBy(school => school.PriceMinor).ThenBy(school => school.Id).FirstOrDefault();

    // The student's own share of tuition after the government's subsidy.
    public static long OwnShare(Nation nation, Agent school) => (long)school.PriceMinor * (100 - nation.TuitionSubsidyPct) / 100;

    public static bool CanEnroll(World world, Agent person)
    {
        if (person.Role != "citizen" || SocietyLabor.IsStudent(person) || person.Graduated
            || OpenSchool(world, person.CountryId) is not { } school)
            return false;
        var nation = world.Society!.Nations[person.CountryId];
        var own = OwnShare(nation, school);
        return SocietyBank.Liquid(world, person) >= own
            && world.Agents[nation.Id + "_government"].CashMinor >= school.PriceMinor - own;
    }

    public static void Enroll(World world, Agent person, string cause)
    {
        var nation = world.Society!.Nations[person.CountryId];
        var school = OpenSchool(world, person.CountryId)!;
        var government = world.Agents[nation.Id + "_government"];
        var own = OwnShare(nation, school);
        SocietyBank.Draw(world, person, own);
        SocietyEconomy.Sell(world, person, school, own, false, 1, "tuition");
        SocietyEconomy.Sell(world, government, school, school.PriceMinor - own, kind: "tuition_subsidy");
        var quit = person.EmployerId;
        person.EmployerId = null; // studying is full time: an employee leaves their job to enroll
        person.SalaryMinor = 0;
        person.OvertimeDays = 0;
        person.UnemployedSinceTick = world.Tick;
        person.StudentUntilTick = world.Tick + SocietyLabor.CourseDays;
        person.SchoolId = school.Id;
        world.Emit("enrolled", "rule", [person.Id, school.Id, nation.Id], new
        {
            tuition_minor = school.PriceMinor, paid_by_student_minor = own, quit_employer = quit,
            graduates_tick = person.StudentUntilTick
        }, cause);
    }

    public static void Graduate(World world, Agent person)
    {
        person.Graduated = true;
        person.StudentUntilTick = -1;
        person.SchoolId = null;
        person.UnemployedSinceTick = world.Tick;
        world.Emit("graduated", "rule", [person.Id, person.CountryId], new
        {
            output_boost = SocietyLabor.GraduateOutputBoost, occupation = person.Occupation
        });
    }
}
