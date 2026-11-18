namespace ScaleCore;

public sealed record LegoBrick(string Name, string ImagePath, double MassGrams, string PartNumber)
{
    public long CountForWeightKg(double weightKg) =>
        Math.Max(0, (long)Math.Round(weightKg * 1000 / MassGrams, MidpointRounding.AwayFromZero));
}

public static class BrickCatalog
{
    public static IReadOnlyList<LegoBrick> All { get; } =
    [
        new("Кирпичик 1×1", "Assets/Bricks/brick_1x1.png", 0.44, "3005"),
        new("Кирпичик 2×2", "Assets/Bricks/brick_2x2.png", 1.35, "3003"),
        new("Кирпичик 2×4", "Assets/Bricks/brick_2x4.png", 2.32, "3001"),
        new("Кирпичик 2×8", "Assets/Bricks/brick_2x8.png", 4.75, "3007")
    ];

    public static LegoBrick PickDifferent(string? previousPartNumber)
    {
        var choices = All.Where(brick => brick.PartNumber != previousPartNumber).ToArray();
        return choices[Random.Shared.Next(choices.Length)];
    }
}
