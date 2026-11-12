using System.Globalization;
using System.Text.RegularExpressions;
using ShiftSense.Domain;

namespace ShiftSense.Agent.Tools;

internal sealed class ToolAuthorizationException(string message) : Exception(message);

internal sealed class ToolValidationException(string message) : Exception(message);

/// <summary>Strict parsing of model-supplied arguments. Anything that doesn't match exactly is rejected.</summary>
internal static partial class ToolGuards
{
    [GeneratedRegex("^E[0-9]{3}$")]
    private static partial Regex EmployeeIdPattern();

    [GeneratedRegex("^S[0-9]{4}$")]
    private static partial Regex ShiftIdPattern();

    [GeneratedRegex("^T-[A-Z]{3}$")]
    private static partial Regex TeamIdPattern();

    public static string EmployeeId(string? value, string paramName)
    {
        var v = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v) || !EmployeeIdPattern().IsMatch(v))
        {
            throw new ToolValidationException($"{paramName} must look like E004 (got '{value}')");
        }
        return v;
    }

    public static string ShiftId(string? value, string paramName)
    {
        var v = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v) || !ShiftIdPattern().IsMatch(v))
        {
            throw new ToolValidationException($"{paramName} must look like S0042 (got '{value}')");
        }
        return v;
    }

    public static string TeamId(string? value, string paramName)
    {
        var v = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v) || !TeamIdPattern().IsMatch(v))
        {
            throw new ToolValidationException($"{paramName} must look like T-BLR (got '{value}')");
        }
        return v;
    }

    public static DateOnly Date(string? value, string paramName)
    {
        if (DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }
        throw new ToolValidationException($"{paramName} must be an ISO date like 2026-11-20 (got '{value}')");
    }

    public static DateOnly? OptionalDate(string? value, string paramName) =>
        string.IsNullOrWhiteSpace(value) ? null : Date(value, paramName);

    public static LeaveType LeaveType(string? value)
    {
        if (Enum.TryParse<LeaveType>(value?.Trim(), ignoreCase: true, out var t) && Enum.IsDefined(t))
        {
            return t;
        }
        var allowed = string.Join(", ", Enum.GetNames<LeaveType>());
        throw new ToolValidationException($"leaveType must be one of {allowed} (got '{value}')");
    }

    public static string? Reason(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var v = value.Trim();
        if (v.Length > 300)
        {
            throw new ToolValidationException("reason must be 300 characters or fewer");
        }
        return v.Length == 0 ? null : v;
    }

    public static int Top(int? value) => value is null ? 5 : Math.Clamp(value.Value, 1, 20);
}
