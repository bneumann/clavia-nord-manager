using NordSampleManager.Protocol.Records;

namespace NordSampleManager.Protocol.Tests;

public class ProgramCategoryTests
{
    // ── FromCode ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(21u, ProgramCategory.Grand)]
    [InlineData(24u, ProgramCategory.EPiano2)]
    [InlineData(27u, ProgramCategory.Clavinet)]
    public void FromCode_ConfirmedCodes_RoundTrip(uint code, ProgramCategory expected)
    {
        Assert.Equal(expected, ProgramCategoryExtensions.FromCode(code));
        Assert.Equal(code, (uint)ProgramCategoryExtensions.FromCode(code));
    }

    [Fact]
    public void FromCode_UnknownCode_ReturnsUndefined()
    {
        Assert.Equal(ProgramCategory.Undefined, ProgramCategoryExtensions.FromCode(0xDEADu));
    }

    [Fact]
    public void FromCode_Zero_ReturnsAcoustic()
    {
        // Acoustic = 0 is the confirmed code from the protocol dump.
        Assert.Equal(ProgramCategory.Acoustic, ProgramCategoryExtensions.FromCode(0u));
    }

    // ── DisplayName ─────────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_Grand_ReturnsGrand()
    {
        Assert.Equal("Grand", ProgramCategory.Grand.DisplayName());
    }

    [Fact]
    public void DisplayName_GuitarPlucked_ReturnsSlashForm()
    {
        Assert.Equal("Guitar/Plucked", ProgramCategory.GuitarPlucked.DisplayName());
    }

    [Fact]
    public void DisplayName_StringCat_ReturnsString()
    {
        Assert.Equal("String", ProgramCategory.StringCat.DisplayName());
    }

    [Fact]
    public void DisplayName_AllCategories_NoneEmpty()
    {
        foreach (var cat in Enum.GetValues<ProgramCategory>())
            Assert.False(string.IsNullOrWhiteSpace(cat.DisplayName()),
                $"{cat} produced an empty display name");
    }

    // ── AllCategories ───────────────────────────────────────────────────────

    [Fact]
    public void AllCategories_Contains19Entries()
    {
        Assert.Equal(19, ProgramCategoryExtensions.AllCategories.Count);
    }

    [Fact]
    public void AllCategories_IsSortedAlphabetically()
    {
        var names = ProgramCategoryExtensions.AllCategories
            .Select(c => c.DisplayName())
            .ToList();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void AllCategories_ContainsAllEnumMembers()
    {
        var all = ProgramCategoryExtensions.AllCategories.ToHashSet();
        foreach (var cat in Enum.GetValues<ProgramCategory>())
            Assert.Contains(cat, all);
    }

    // ── Enum uniqueness ─────────────────────────────────────────────────────

    [Fact]
    public void EnumValues_AreAllUnique()
    {
        var values = Enum.GetValues<ProgramCategory>().Select(c => (uint)c).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void ConfirmedCodes_NotOverriddenByPlaceholders()
    {
        // Regression: placeholder values 1-16 must not collide with confirmed codes.
        var confirmed = new uint[] { 21, 24, 27 };
        var placeholders = Enum.GetValues<ProgramCategory>()
            .Select(c => (uint)c)
            .Except(confirmed);
        foreach (var p in placeholders)
            Assert.DoesNotContain(p, confirmed);
    }
}
