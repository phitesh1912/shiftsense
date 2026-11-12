namespace ShiftSense.Domain;

/// <summary>
/// Deterministic fictional dataset for "Acme Retail": 2 regions, 4 teams, 25 employees, two weeks
/// of shifts starting on a fixed Monday. Everything is derived from a seeded RNG so tests, the
/// demo and the eval all see the same world.
/// </summary>
public static class SeedData
{
    /// <summary>The Monday the two-week roster starts. Chosen so "today" in the demo sits mid-roster.</summary>
    public static readonly DateOnly RosterStart = new(2026, 11, 16);

    /// <summary>Fixed "now" used by the demo and tests: Wednesday of week 1, 09:00.</summary>
    public static readonly DateTime DemoNow = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    public const int RosterDays = 14;

    public static readonly IReadOnlyList<Team> Teams =
    [
        new() { Id = "T-BLR", Name = "Bengaluru Whitefield store", Region = Region.IN, MinHeadcountPerSlot = 3 },
        new() { Id = "T-HYD", Name = "Hyderabad warehouse", Region = Region.IN, MinHeadcountPerSlot = 2 },
        new() { Id = "T-AUS", Name = "Austin store", Region = Region.US, MinHeadcountPerSlot = 3 },
        new() { Id = "T-DEN", Name = "Denver warehouse", Region = Region.US, MinHeadcountPerSlot = 2 },
    ];

    public static IReadOnlyList<Employee> Employees() => BuildEmployees();

    public static IReadOnlyList<Shift> Shifts(IReadOnlyList<Employee> employees) => BuildShifts(employees);

    public static IReadOnlyList<LeaveBalance> LeaveBalances(IReadOnlyList<Employee> employees) =>
        employees.SelectMany(BalancesFor).ToList();

    public static IReadOnlyList<LeaveRequest> LeaveRequests() =>
    [
        // An approved leave that removes an employee from the roster in week 2 — used to test
        // that swap candidates on leave are excluded.
        new()
        {
            Id = "LR-0001", EmployeeId = "E004", Type = LeaveType.Annual,
            StartDate = new DateOnly(2026, 11, 23), EndDate = new DateOnly(2026, 11, 25), Days = 3,
            Reason = "Family trip", Status = RequestStatus.Approved,
            CreatedAt = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
            DecidedAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), DecidedBy = "E001",
        },
        new()
        {
            Id = "LR-0002", EmployeeId = "E015", Type = LeaveType.Sick,
            StartDate = new DateOnly(2026, 11, 17), EndDate = new DateOnly(2026, 11, 17), Days = 1,
            Reason = "Flu", Status = RequestStatus.Pending,
            CreatedAt = new DateTime(2026, 11, 17, 6, 30, 0, DateTimeKind.Utc),
        },
    ];

    // ---------------------------------------------------------------------------------------

    private static readonly string[] IndiaNames =
    [
        "Priya Nair", "Arjun Mehta", "Kavya Reddy", "Rohan Iyer", "Sneha Kulkarni", "Vikram Rao",
        "Ananya Das", "Karthik Menon", "Divya Pillai", "Aditya Joshi", "Meera Krishnan", "Nikhil Bose",
        "Pooja Shetty",
    ];

    private static readonly string[] UsNames =
    [
        "Jordan Blake", "Taylor Reed", "Morgan Hayes", "Casey Brooks", "Riley Parker", "Avery Collins",
        "Quinn Foster", "Dakota Hughes", "Skyler Morgan", "Emerson Price", "Reese Bennett", "Harper Cole",
    ];

    private static readonly string[][] SkillPool =
    [
        [Skills.Cashier],
        [Skills.Cashier, Skills.FirstAid],
        [Skills.Forklift],
        [Skills.Forklift, Skills.FirstAid],
        [Skills.Cashier, Skills.Barista],
        [Skills.Pharmacy, Skills.Cashier],
        [Skills.Cashier, Skills.Supervisor, Skills.FirstAid],
    ];

    private static List<Employee> BuildEmployees()
    {
        var rng = new Random(42);
        var list = new List<Employee>();
        var id = 1;

        void AddRegion(string[] names, Region region, string[] teamIds, decimal baseRate, int maxWeekly)
        {
            var managerId = $"E{id:000}";
            list.Add(new Employee
            {
                Id = managerId, Name = names[0], Email = Email(names[0]), Region = region, Role = Role.Manager,
                TeamId = teamIds[0], ManagerId = null, JoinDate = new DateOnly(2019, 3, 1),
                HourlyRate = baseRate * 1.8m, MaxWeeklyHours = maxWeekly,
                SkillIds = [Skills.Cashier, Skills.Supervisor, Skills.FirstAid, Skills.Forklift],
            });
            id++;
            for (var i = 1; i < names.Length; i++)
            {
                var team = teamIds[i % teamIds.Length];
                var skills = SkillPool[rng.Next(SkillPool.Length)];
                // warehouse teams always need forklift coverage; make sure some have it
                if (team.EndsWith("HYD", StringComparison.Ordinal) || team.EndsWith("DEN", StringComparison.Ordinal))
                {
                    skills = i % 3 == 0 ? [Skills.Forklift, Skills.FirstAid] : [Skills.Forklift];
                }
                list.Add(new Employee
                {
                    Id = $"E{id:000}", Name = names[i], Email = Email(names[i]), Region = region,
                    Role = Role.Employee, TeamId = team, ManagerId = managerId,
                    JoinDate = new DateOnly(2021 + rng.Next(5), 1 + rng.Next(12), 1 + rng.Next(28)),
                    HourlyRate = baseRate + rng.Next(0, 6) * (baseRate / 20m),
                    MaxWeeklyHours = maxWeekly,
                    SkillIds = [.. skills],
                });
                id++;
            }
        }

        AddRegion(IndiaNames, Region.IN, ["T-BLR", "T-HYD"], baseRate: 250m, maxWeekly: 48);
        AddRegion(UsNames, Region.US, ["T-AUS", "T-DEN"], baseRate: 18m, maxWeekly: 40);
        return list;
    }

    private static string Email(string name) =>
        name.ToLowerInvariant().Replace(' ', '.') + "@acme.example";

    /// <summary>
    /// Three 8-hour slots per day. Each employee works 4 of 7 days in a fixed slot for the
    /// fortnight (32h/week), which leaves one extra shift of headroom under the US 40h cap and
    /// two under the Indian 48h cap, so swaps are usually possible and tests can still push
    /// someone over a limit deliberately.
    /// </summary>
    private static List<Shift> BuildShifts(IReadOnlyList<Employee> employees)
    {
        var rng = new Random(7);
        var shifts = new List<Shift>();
        var seq = 1;
        (int StartHour, string Name)[] slots = [(6, "morning"), (14, "evening"), (22, "night")];

        foreach (var team in Teams)
        {
            var staff = employees.Where(e => e.TeamId == team.Id && !e.IsManager).ToList();
            for (var s = 0; s < staff.Count; s++)
            {
                var emp = staff[s];
                var slot = slots[s % slots.Length];
                var offDays = Enumerable.Range(0, 7).OrderBy(_ => rng.Next()).Take(3).ToHashSet();
                for (var day = 0; day < RosterDays; day++)
                {
                    if (offDays.Contains(day % 7))
                    {
                        continue;
                    }
                    var date = RosterStart.AddDays(day);
                    var start = date.ToDateTime(new TimeOnly(slot.StartHour, 0));
                    var required = new HashSet<string>();
                    if (team.Id.EndsWith("HYD", StringComparison.Ordinal) || team.Id.EndsWith("DEN", StringComparison.Ordinal))
                    {
                        required.Add(Skills.Forklift);
                    }
                    else
                    {
                        required.Add(Skills.Cashier);
                    }
                    if (slot.Name == "night")
                    {
                        required.Add(Skills.FirstAid);
                    }
                    // Only create the shift if the employee can actually work it; otherwise the
                    // seed would violate its own rules. Night shifts without first-aid are
                    // reassigned to a plain slot requirement.
                    if (!emp.HasSkills(required))
                    {
                        required.Remove(Skills.FirstAid);
                        if (!emp.HasSkills(required))
                        {
                            continue;
                        }
                    }
                    shifts.Add(new Shift
                    {
                        Id = $"S{seq++:0000}",
                        TeamId = team.Id,
                        EmployeeId = emp.Id,
                        Start = start,
                        End = start.AddHours(8),
                        RequiredSkillIds = required,
                    });
                }
            }
        }

        // One deliberately unassigned shift per region so the coverage solver has work to do.
        shifts.Add(new Shift
        {
            Id = $"S{seq++:0000}", TeamId = "T-BLR", EmployeeId = null,
            Start = RosterStart.AddDays(4).ToDateTime(new TimeOnly(14, 0)),
            End = RosterStart.AddDays(4).ToDateTime(new TimeOnly(22, 0)),
            RequiredSkillIds = [Skills.Cashier],
        });
        shifts.Add(new Shift
        {
            Id = $"S{seq++:0000}", TeamId = "T-AUS", EmployeeId = null,
            Start = RosterStart.AddDays(5).ToDateTime(new TimeOnly(6, 0)),
            End = RosterStart.AddDays(5).ToDateTime(new TimeOnly(14, 0)),
            RequiredSkillIds = [Skills.Cashier],
        });
        return shifts;
    }

    private static IEnumerable<LeaveBalance> BalancesFor(Employee e)
    {
        var rng = new Random(e.Id.GetHashCode(StringComparison.Ordinal));
        yield return new LeaveBalance
        {
            EmployeeId = e.Id, Type = LeaveType.Annual,
            Days = e.Region == Region.IN ? 8 + rng.Next(0, 20) : 3 + rng.Next(0, 10),
            UsedThisYear = rng.Next(0, 8),
        };
        yield return new LeaveBalance
        {
            EmployeeId = e.Id, Type = LeaveType.Sick,
            Days = 4 + rng.Next(0, 8), UsedThisYear = rng.Next(0, 4),
        };
        if (e.Region == Region.IN)
        {
            yield return new LeaveBalance
            {
                EmployeeId = e.Id, Type = LeaveType.Casual, Days = rng.Next(0, 7), UsedThisYear = rng.Next(0, 4),
            };
        }
    }
}
