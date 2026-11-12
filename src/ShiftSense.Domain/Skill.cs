namespace ShiftSense.Domain;

/// <summary>A certification or capability a shift can require, e.g. "forklift".</summary>
public sealed record Skill(string Id, string Name);

public static class Skills
{
    public const string Cashier = "cashier";
    public const string Forklift = "forklift";
    public const string FirstAid = "first-aid";
    public const string Supervisor = "supervisor";
    public const string Pharmacy = "pharmacy";
    public const string Barista = "barista";

    public static readonly IReadOnlyList<Skill> All =
    [
        new(Cashier, "Cashier"),
        new(Forklift, "Forklift certified"),
        new(FirstAid, "First aid"),
        new(Supervisor, "Shift supervisor"),
        new(Pharmacy, "Pharmacy counter"),
        new(Barista, "Barista"),
    ];
}
