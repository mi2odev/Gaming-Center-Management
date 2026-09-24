using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;

namespace GamingCenter.Tests;

public class BillingCalculatorTests
{
    private static readonly BillingRules Exact = new(0, RoundingMode.Up, 0, 1m);
    private static readonly BillingRules PerMinute = new(60, RoundingMode.Up, 0, 1m);

    [Fact]
    public void Open_session_exact_billing_matches_spec_example()
    {
        // 1h 53m 42s at 200 DA/h = 379 DA
        var played = new TimeSpan(1, 53, 42);
        Assert.Equal(379m, BillingCalculator.Cost(played, 200m, Exact));
    }

    [Fact]
    public void Per_minute_round_up_matches_settings_preview()
    {
        // 1h 53m 42s -> 114 min at 300 DA/h = 570 DA
        Assert.Equal(570m, BillingCalculator.Cost(new TimeSpan(1, 53, 42), 300m, PerMinute));
    }

    [Theory]
    [InlineData(15, 100)]
    [InlineData(30, 200)]
    [InlineData(45, 300)]
    [InlineData(60, 400)]
    [InlineData(16, 200)] // a started unit is charged when rounding up
    public void Fifteen_minute_units_match_spec_table(int minutes, int expected)
    {
        var rules = new BillingRules(900, RoundingMode.Up, 0, 1m);
        Assert.Equal(expected, BillingCalculator.Cost(TimeSpan.FromMinutes(minutes), 400m, rules));
    }

    [Fact]
    public void Rounding_modes_behave()
    {
        var t = TimeSpan.FromMinutes(22); // 1.47 units of 15 min
        Assert.Equal(2 * 900, BillingCalculator.BillableSeconds(t, new BillingRules(900, RoundingMode.Up, 0, 1)));
        Assert.Equal(1 * 900, BillingCalculator.BillableSeconds(t, new BillingRules(900, RoundingMode.Down, 0, 1)));
        Assert.Equal(1 * 900, BillingCalculator.BillableSeconds(t, new BillingRules(900, RoundingMode.Nearest, 0, 1)));
        Assert.Equal(2 * 900, BillingCalculator.BillableSeconds(TimeSpan.FromMinutes(23), new BillingRules(900, RoundingMode.Nearest, 0, 1)));
    }

    [Fact]
    public void Minimum_charge_applies_to_short_sessions_only()
    {
        var rules = new BillingRules(60, RoundingMode.Up, 15, 1m);
        Assert.Equal(75m, BillingCalculator.Cost(TimeSpan.FromMinutes(3), 300m, rules));
        Assert.Equal(0m, BillingCalculator.Cost(TimeSpan.Zero, 300m, rules));
        Assert.Equal(100m, BillingCalculator.Cost(TimeSpan.FromMinutes(20), 300m, rules));
    }

    [Fact]
    public void Money_rounding_step()
    {
        Assert.Equal(380m, BillingCalculator.RoundMoney(378.3m, 5m));
        Assert.Equal(375m, BillingCalculator.RoundMoney(376.2m, 5m));
        Assert.Equal(378.33m, BillingCalculator.RoundMoney(378.333m, 0m));
    }

    [Fact]
    public void Budget_converts_to_time()
    {
        Assert.Equal(new TimeSpan(2, 30, 0), BillingCalculator.TimeForBudget(500m, 200m));
        Assert.Equal(new TimeSpan(1, 40, 0), BillingCalculator.TimeForBudget(500m, 300m));
    }

    private static GamingSession Session(SessionMode mode, DateTime start, decimal rate = 200m, int? minutes = null, decimal? budget = null)
    {
        var s = new GamingSession { Mode = mode, StartTime = start, HourlyRate = rate, PlannedMinutes = minutes, Budget = budget, Status = SessionStatus.Running };
        s.ApplyRules(Exact);
        return s;
    }

    [Fact]
    public void Pauses_are_excluded_from_play_time()
    {
        var start = new DateTime(2026, 9, 24, 18, 0, 0);
        var s = Session(SessionMode.Open, start);
        s.Pauses.Add(new SessionPause { StartTime = start.AddMinutes(45), EndTime = start.AddMinutes(60) });
        var now = start.AddMinutes(90);
        Assert.Equal(TimeSpan.FromMinutes(75), s.PlayedTime(now));
        Assert.Equal(250m, s.GamingCost(now)); // 1h15 at 200
    }

    [Fact]
    public void Open_pause_freezes_timer_and_cost()
    {
        var start = new DateTime(2026, 9, 24, 18, 0, 0);
        var s = Session(SessionMode.Open, start);
        s.Pauses.Add(new SessionPause { StartTime = start.AddMinutes(30) });
        Assert.Equal(s.PlayedTime(start.AddMinutes(40)), s.PlayedTime(start.AddMinutes(90)));
        Assert.Equal(100m, s.GamingCost(start.AddHours(5)));
    }

    [Fact]
    public void Fixed_duration_charges_purchased_time_even_when_leaving_early()
    {
        var start = new DateTime(2026, 9, 24, 18, 0, 0);
        var s = Session(SessionMode.FixedDuration, start, minutes: 120);
        Assert.Equal(400m, s.GamingCost(start.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(90), s.RemainingTime(start.AddMinutes(30)));
        Assert.False(s.IsTimeUp(start.AddMinutes(119)));
        Assert.True(s.IsTimeUp(start.AddMinutes(120)));
        // Overstaying without an extension is billed at the same rate.
        Assert.Equal(500m, s.GamingCost(start.AddMinutes(150)));
    }

    [Fact]
    public void Fixed_budget_never_exceeds_budget()
    {
        var start = new DateTime(2026, 9, 24, 18, 0, 0);
        var s = Session(SessionMode.FixedBudget, start, budget: 500m);
        Assert.Equal(new TimeSpan(2, 30, 0), s.AllowedTime(start));
        Assert.Equal(100m, s.GamingCost(start.AddMinutes(30)));
        Assert.Equal(500m, s.GamingCost(start.AddHours(4)));
        Assert.Equal(TimeSpan.FromMinutes(10), s.RemainingTime(start.AddMinutes(140)));
    }

    [Fact]
    public void Price_is_locked_on_the_session()
    {
        var start = new DateTime(2026, 9, 24, 18, 0, 0);
        var station = new GamingStation { HourlyRate = 300m };
        var s = Session(SessionMode.Open, start, rate: station.HourlyRate);
        station.HourlyRate = 350m;
        Assert.Equal(300m, s.GamingCost(start.AddHours(1)));
    }
}
