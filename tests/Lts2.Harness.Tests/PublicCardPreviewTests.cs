using System.Linq;
using System.Text.Json;
using Lts2.Harness;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using Sts2Recorder;
using Xunit;

namespace Lts2.Harness.Tests;

public sealed class PublicCardPreviewTests
{
    [Theory]
    [InlineData("Nimble", "DefendIronclad", "Block", 2, 5, 7)]
    [InlineData("Sharp", "StrikeIronclad", "Damage", 2, 6, 8)]
    [InlineData("Corrupted", "StrikeIronclad", "Damage", 1, 6, 9)]
    [InlineData("Instinct", "StrikeIronclad", "Damage", 1, 6, 12)]
    [InlineData("TezcatarasEmber", "StrikeIronclad", "Damage", 1, 6, 9)]
    public void EnchantedPublicCard_RefreshesDisplayValuesWithoutMutatingCardOrRng(
        string enchantment, string cardName, string variable, int amount, int expectedBase, int expectedPreview)
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        var card = host.Run.Players[0].Deck.Cards.First(c => c.GetType().Name == cardName);
        var enchantType = typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Enchantments." + enchantment, true)!;
        var enchant = (EnchantmentModel)typeof(ModelDb).GetMethods()
            .Single(m => m.Name == "Enchantment" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(enchantType).Invoke(null, null)!;
        CardCmd.Enchant(enchant.ToMutable(), card, amount);
        var original = card.DynamicVars.Values.Select(v => (v.Name, v.BaseValue, v.EnchantedValue, v.PreviewValue)).ToArray();
        string rng = JsonDocument.Parse(host.ToSaveJson()).RootElement.GetProperty("rng").GetRawText();
        var projection = new CardProjection();
        for (int read = 0; read < 3; read++)
        {
            var projected = JsonSerializer.SerializeToElement(projection.Read(card));
            var value = projected.GetProperty("facts").GetProperty("dynamicVars").EnumerateArray()
                .Single(v => v.GetProperty("name").GetString() == variable);
            Assert.Equal(expectedBase, value.GetProperty("baseValue").GetDecimal());
            Assert.Equal(expectedPreview, value.GetProperty("enchantedValue").GetDecimal());
            Assert.Equal(expectedPreview, value.GetProperty("previewValue").GetDecimal());
            Assert.Equal(original, card.DynamicVars.Values.Select(v => (v.Name, v.BaseValue, v.EnchantedValue, v.PreviewValue)).ToArray());
        }
        Assert.Equal(rng, JsonDocument.Parse(host.ToSaveJson()).RootElement.GetProperty("rng").GetRawText());
        Assert.Empty(errors.Errors);
    }
}
