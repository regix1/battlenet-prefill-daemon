namespace BattleNetPrefill.Test;

public sealed class TactProductListTests
{
    [Theory]
    [InlineData("wowf", "World of Warcraft: Forever")]
    [InlineData("wow_anniversary", "Burning Crusade Anniversary")]
    [InlineData("aris", "DOOM: The Dark Ages")]
    [InlineData("ark", "The Outer Worlds 2")]
    [InlineData("lbra", "Tony Hawk's Pro Skater 3 + 4")]
    [InlineData("lyra", "The Witcher 3: Wild Hunt — Remastered")]
    [InlineData("malb", "Cyberpunk 2077")]
    public void LiveProductCanBeSelected(string productCode, string displayName)
    {
        Assert.True(TactProduct.IsValidEnumValue(productCode), $"'{productCode}' is not in the product list");
        Assert.Contains(TactProduct.AllEnumValues, product => product.ProductCode == productCode);
        Assert.Equal(displayName, TactProduct.Parse(productCode).DisplayName);
    }

    [Fact]
    public void ProductWhoseOnlyDownloadTagIsReservedSelectsIt()
    {
        // The aris download manifest tags every file with "_reserved_" and nothing else, so the
        // default tag list would select no tag at all.
        Assert.True(TactProduct.IsValidEnumValue("aris"), "'aris' is not in the product list");
        Assert.Equal(new[] { "_reserved_" }, TactProduct.Parse("aris").DefaultTags);
    }
}
