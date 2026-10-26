using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using Lts2.Harness;
using Xunit;

namespace Lts2.Harness.Tests;

public sealed class EventConnectionTests
{
    private static GameHost Enter(string name)
    {
        var host = TestNav.StartOnMap("TESTSEED");
        TestNav.AddGold(host, 999);
        host.EnterEventDebug(ModelDb.ActsByIndex.SelectMany(a => a).SelectMany(a => a.AllEvents)
            .Concat(ModelDb.AllSharedEvents).First(e => e.GetType().Name == name));
        return host;
    }

    [Fact]
    public void FakeMerchant_ClosedInventoryDoesNotLeakStock_AndCloseDoesNotLeave()
    {
        var host = Enter("FakeMerchant");
        Assert.Equal(GamePhase.Shop, host.GetState().Phase);
        Assert.False(host.GetState().Shop!.InventoryOpen);
        Assert.Empty(host.GetState().Shop!.Items);
        Assert.DoesNotContain(host.ListOptions(), o => o.Kind == OptionKind.BuyShopItem || o.Kind == OptionKind.MoveTo);
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.OpenShop));
        Assert.True(host.GetState().Shop!.InventoryOpen);
        Assert.Equal(6, host.GetState().Shop!.Items.Count);
        for (int i = 0; i < 2; i++)
        {
            var buy = host.ListOptions().First(o => o.Kind == OptionKind.BuyShopItem);
            int gold = host.Run.Players[0].Gold;
            host.Apply(buy);
            Assert.Equal(gold - buy.ShopItemCost, host.Run.Players[0].Gold);
            Assert.Contains(buy.ShopItemId, host.GetState().Players[0].Relics);
        }
        Assert.Equal(4, host.GetState().Shop!.Items.Count);
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.CloseShop));
        Assert.Equal(GamePhase.Shop, host.GetState().Phase);
        Assert.Empty(host.GetState().Shop!.Items);
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.OpenShop));
        Assert.Equal(4, host.GetState().Shop!.Items.Count);
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.LeaveShop));
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FakeMerchant_FoulPotionIsUsableOnlyWhenClosed_AndUsesRealFight(bool buyFirst)
    {
        using var errors = new LogErrorSink();
        var host = Enter("FakeMerchant");
        TestNav.GivePotion(host, "FoulPotion");
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.OpenShop));
        Assert.DoesNotContain(host.ListOptions(), o => o.Kind == OptionKind.UsePotion && o.PotionId == "FOUL_POTION");
        if (buyFirst) host.Apply(host.ListOptions().First(o => o.Kind == OptionKind.BuyShopItem));
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.CloseShop));
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.UsePotion && o.PotionId == "FOUL_POTION"));
        Assert.Equal(GamePhase.Combat, host.GetState().Phase);
        Assert.DoesNotContain("FOUL_POTION", host.GetState().Players[0].Potions);
        Assert.NotEmpty(host.GetState().Combat!.Enemies);
        TestNav.SetHp(host, maxHp: 9999, currentHp: 9999);
        var end = AutoPlayer.Advance(host, s => s.Phase is GamePhase.Map or GamePhase.GameOver, maxSteps: 1500);
        Assert.Equal(GamePhase.Map, end.Phase);
        Assert.Contains("FAKE_MERCHANTS_RUG", end.Players[0].Relics);
        Assert.True(end.Players[0].Relics.Count >= 7);
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public void Trial_DoubleDownIsPublic_AndRealAbandonEndsInDeath()
    {
        using var errors = new LogErrorSink();
        var host = Enter("Trial");
        host.Apply(host.ListOptions().Single(o => o.Description.Contains("REJECT")));
        var option = host.GetState().Event!.Options.Single(o => o.TextKey.EndsWith("DOUBLE_DOWN"));
        Assert.True(option.WillKillPlayer);
        host.Apply(host.ListOptions().Single(o => o.EventOptionIndex == option.Index));
        Assert.Equal(GamePhase.GameOver, host.GetState().Phase);
        Assert.False(host.GetState().IsVictory);
        Assert.Empty(host.ListOptions());
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public async System.Threading.Tasks.Task Architect_ProceedUsesGameWin_AndZeroHpIsVictory()
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        string mapSave = host.ToSaveJson();
        await MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterAct(2, false);
        host.EnterEventDebug(ModelDb.Event<TheArchitect>());
        Assert.Equal(GamePhase.Event, host.GetState().Phase);
        for (int step = 0; step < 30 && !host.GetState().IsGameOver; step++)
        {
            var proceed = Assert.Single(host.ListOptions(), o => o.Kind == OptionKind.ChooseEventOption);
            System.Console.WriteLine($"Architect step {step}: {proceed.Description}");
            host.Apply(proceed);
        }
        Assert.Equal(GamePhase.GameOver, host.GetState().Phase);
        Assert.True(host.GetState().IsVictory);
        Assert.Equal(0, host.GetState().Players[0].CurrentHp);
        MegaCrit.Sts2.Core.Runs.RunManager.Instance.GetType().GetMethod("OnEnded",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(MegaCrit.Sts2.Core.Runs.RunManager.Instance, new object[] { false });
        Assert.True(host.GetState().IsVictory);
        Assert.Empty(errors.Errors);
        var fresh = GameHost.RestoreFromJson(mapSave, "TESTSEED");
        Assert.False(fresh.GetState().IsVictory);
        fresh.EnterEventDebug(ModelDb.Event<Trial>());
        fresh.Apply(fresh.ListOptions().Single(o => o.Description.Contains("REJECT")));
        fresh.Apply(fresh.ListOptions().Single(o => o.Description.Contains("DOUBLE_DOWN")));
        Assert.True(fresh.GetState().IsGameOver);
        Assert.False(fresh.GetState().IsVictory);
        var another = TestNav.StartOnMap("TESTSEED");
        another.EnterEventDebug(ModelDb.Event<Trial>());
        another.Apply(another.ListOptions().Single(o => o.Description.Contains("REJECT")));
        another.Apply(another.ListOptions().Single(o => o.Description.Contains("DOUBLE_DOWN")));
        Assert.True(another.GetState().IsGameOver);
        Assert.False(another.GetState().IsVictory);
    }
}
