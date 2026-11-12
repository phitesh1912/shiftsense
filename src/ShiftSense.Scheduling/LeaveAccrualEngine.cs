using ShiftSense.Domain;

namespace ShiftSense.Scheduling;

/// <summary>
/// Monthly accrual with pro-rating for the joining month and a carry-forward cap at year end.
/// Accrual for a month is credited once that month is *complete* (i.e. on the 1st of the next
/// month), which matches how most HRIS systems post leave.
/// </summary>
public static class LeaveAccrualEngine
{
    /// <summary>
    /// Days accrued between the later of <paramref name="joinDate"/> and <paramref name="leaveYearStart"/>
    /// and <paramref name="asOf"/>, counting only completed months and pro-rating the first
    /// partial month by days employed / days in month. Result is rounded to 2 decimals.
    /// </summary>
    public static decimal AccruedDays(PayPolicy policy, DateOnly joinDate, DateOnly asOf, DateOnly leaveYearStart)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var start = joinDate > leaveYearStart ? joinDate : leaveYearStart;
        if (asOf <= start)
        {
            return 0m;
        }

        decimal total = 0m;
        var monthStart = new DateOnly(start.Year, start.Month, 1);
        while (true)
        {
            var nextMonth = monthStart.AddMonths(1);
            if (nextMonth > asOf)
            {
                break; // month not yet complete
            }
            var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            var firstWorked = start > monthStart ? start : monthStart;
            var daysWorked = daysInMonth - (firstWorked.Day - 1);
            var fraction = (decimal)daysWorked / daysInMonth;
            total += policy.AnnualLeaveAccrualPerMonth * fraction;
            monthStart = nextMonth;
        }
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Balance that survives the leave-year boundary.</summary>
    public static decimal CarryForward(PayPolicy policy, decimal endOfYearBalance)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Math.Max(0m, Math.Min(endOfYearBalance, policy.AnnualLeaveCarryForwardCap));
    }

    /// <summary>Working days (Mon–Fri) in an inclusive date range; what a leave request consumes.</summary>
    public static int WorkingDays(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            return 0;
        }
        var days = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days++;
            }
        }
        return days;
    }
}
