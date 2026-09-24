using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using GamingCenter.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GamingCenter.Tests;

public sealed class TestClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 9, 24, 18, 0, 0);
}

public sealed class TestUser : ICurrentUser
{
    public UserDto? User { get; set; } = new(1, "admin", "Administrator", UserRole.Admin, true, null);
}

/// <summary>Real SQLite (in-memory) so transactions, constraints and migrations are exercised.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    public TestClock Clock { get; } = new();
    public TestUser User { get; } = new();
    public IDbContextFactory<GamingCenterDbContext> Factory { get; }
    public SettingsService Settings { get; }
    public SessionService Sessions { get; }
    public StationService Stations { get; }
    public ProductService Products { get; }
    public ReportService Reports { get; }
    public CustomerService Customers { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<GamingCenterDbContext>().UseSqlite(_connection).Options;
        Factory = new InMemoryFactory(options);
        using (var db = Factory.CreateDbContext())
            DbInitializer.InitializeAsync(db, seedDemoData: true).GetAwaiter().GetResult();

        Settings = new SettingsService(Factory, Clock, User);
        Settings.LoadAsync().GetAwaiter().GetResult();
        Sessions = new SessionService(Factory, Clock, User, Settings);
        Stations = new StationService(Factory, Clock, User);
        Products = new ProductService(Factory, Clock, User);
        Customers = new CustomerService(Factory, Clock, User);
        Reports = new ReportService(Factory, Clock, User, Settings, Products);
    }

    public async Task<StationDto> Station(string name) => (await Stations.GetAllAsync()).First(s => s.Name == name);
    public async Task<ProductDto> Product(string name) => (await Products.GetAllAsync()).First(p => p.Name == name);

    public void Dispose() => _connection.Dispose();

    private sealed class InMemoryFactory(DbContextOptions<GamingCenterDbContext> options) : IDbContextFactory<GamingCenterDbContext>
    {
        public GamingCenterDbContext CreateDbContext() => new(options);
    }
}

public class SessionServiceTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Full_operator_workflow_open_session_with_products()
    {
        var ps5 = await _t.Station("PS5 #04");
        var cola = await _t.Product("Coca-Cola 33cl");

        var s = await _t.Sessions.StartAsync(new StartSessionRequest(ps5.Id, null, SessionMode.Open, null, null));
        Assert.Equal(300m, s.HourlyRate);

        _t.Clock.Now = _t.Clock.Now.AddMinutes(30);
        s = await _t.Sessions.AddProductAsync(s.Id, cola.Id, 2);
        Assert.Equal(300m, s.ProductsCost());
        Assert.Equal(cola.Stock - 2, (await _t.Product("Coca-Cola 33cl")).Stock);

        _t.Clock.Now = _t.Clock.Now.AddMinutes(30);
        var payment = await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 1000m, null));

        Assert.Equal(300m, payment.GamingAmount);   // 1h at 300 DA/h
        Assert.Equal(300m, payment.ProductsAmount);
        Assert.Equal(600m, payment.TotalAmount);
        Assert.Equal(400m, payment.ChangeGiven);
        Assert.Equal("2026-0924-001", payment.ReceiptNumber);
        Assert.Empty(await _t.Sessions.GetLiveSessionsAsync());

        var receipt = await _t.Reports.GetReceiptAsync(s.Id);
        Assert.NotNull(receipt);
        Assert.Equal(600m, receipt!.Total);
        Assert.Single(receipt.Lines);
    }

    [Fact]
    public async Task Price_change_does_not_affect_running_session()
    {
        var ps5 = await _t.Station("PS5 #02");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(ps5.Id, null, SessionMode.Open, null, null));
        await _t.Stations.SaveAsync(new SaveStationRequest(ps5.Id, ps5.Name, ps5.Number, ps5.StationTypeId, ps5.Brand, ps5.Model,
            ps5.ImagePath, 350m, ps5.Description, ps5.Location, ps5.ControllerCount, ps5.State, true));

        _t.Clock.Now = _t.Clock.Now.AddHours(1);
        var payment = await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Card, 0, null));
        Assert.Equal(300m, payment.GamingAmount);

        var history = await _t.Stations.GetPriceHistoryAsync(ps5.Id);
        Assert.Contains(history, h => h.OldRate == 300m && h.NewRate == 350m);
    }

    [Fact]
    public async Task Pause_and_resume_are_recorded()
    {
        var st = await _t.Station("PS5 #01");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        _t.Clock.Now = _t.Clock.Now.AddMinutes(45);
        s = await _t.Sessions.PauseAsync(s.Id);
        Assert.Equal(SessionStatus.Paused, s.Status);
        _t.Clock.Now = _t.Clock.Now.AddMinutes(15);
        s = await _t.Sessions.ResumeAsync(s.Id);
        _t.Clock.Now = _t.Clock.Now.AddMinutes(30);

        Assert.Single(s.Pauses);
        Assert.Equal(TimeSpan.FromMinutes(75), s.PlayedTime(_t.Clock.Now));
    }

    [Fact]
    public async Task Cannot_start_two_sessions_on_one_station()
    {
        var st = await _t.Station("PC #01");
        await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        await Assert.ThrowsAsync<BusinessException>(() => _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null)));
    }

    [Fact]
    public async Task Disabled_station_cannot_start_and_history_is_kept()
    {
        var st = await _t.Station("PS4 #02");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.FixedDuration, 60, null));
        await Assert.ThrowsAsync<BusinessException>(() => _t.Stations.SetActiveAsync(st.Id, false));
        _t.Clock.Now = _t.Clock.Now.AddMinutes(20);
        await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 0, null));

        await _t.Stations.DeleteAsync(st.Id);
        await Assert.ThrowsAsync<BusinessException>(() => _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null)));

        var history = await _t.Reports.GetHistoryAsync(_t.Clock.Now.Date, _t.Clock.Now.Date.AddDays(1));
        var row = Assert.Single(history);
        Assert.Equal("PS4 #02", row.StationName);
        Assert.Equal(200m, row.GamingTotal); // fixed 1h at 200 DA/h even though they left after 20 min
    }

    [Fact]
    public async Task Out_of_stock_is_refused_and_nothing_changes()
    {
        var st = await _t.Station("PS5 #03");
        var sandwich = await _t.Product("Sandwich");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        await Assert.ThrowsAsync<BusinessException>(() => _t.Sessions.AddProductAsync(s.Id, sandwich.Id, sandwich.Stock + 1));
        Assert.Equal(sandwich.Stock, (await _t.Product("Sandwich")).Stock);
        Assert.Empty((await _t.Sessions.GetAsync(s.Id))!.Products);
    }

    [Fact]
    public async Task Reducing_quantity_returns_stock()
    {
        var st = await _t.Station("PS5 #03");
        var chips = await _t.Product("Chips");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        s = await _t.Sessions.AddProductAsync(s.Id, chips.Id, 3);
        s = await _t.Sessions.SetProductQuantityAsync(s.Products[0].Id, 1);
        Assert.Equal(chips.Stock - 1, (await _t.Product("Chips")).Stock);
        s = await _t.Sessions.SetProductQuantityAsync(s.Products[0].Id, 0);
        Assert.Empty(s.Products);
        Assert.Equal(chips.Stock, (await _t.Product("Chips")).Stock);
    }

    [Fact]
    public async Task Budget_session_extension_and_auto_stop()
    {
        var st = await _t.Station("PS4 #01"); // 200 DA/h
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.FixedBudget, null, 200m));
        Assert.Equal(TimeSpan.FromHours(1), s.AllowedTime(_t.Clock.Now));

        _t.Clock.Now = _t.Clock.Now.AddMinutes(60);
        s = await _t.Sessions.StopPlayAsync(s.Id, _t.Clock.Now);
        Assert.Equal(SessionStatus.AwaitingPayment, s.Status);

        // Customer tops up 5 minutes later; the stopped gap is not billed.
        _t.Clock.Now = _t.Clock.Now.AddMinutes(5);
        s = await _t.Sessions.AddBudgetAsync(s.Id, 100m);
        Assert.Equal(SessionStatus.Running, s.Status);
        Assert.Equal(TimeSpan.FromMinutes(60), s.PlayedTime(_t.Clock.Now));

        _t.Clock.Now = _t.Clock.Now.AddMinutes(45);
        var payment = await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 300m, null));
        Assert.Equal(300m, payment.GamingAmount); // capped at budget
    }

    [Fact]
    public async Task Cancel_returns_products_and_is_not_revenue()
    {
        var st = await _t.Station("Xbox #01");
        var water = await _t.Product("Water 50cl");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        await _t.Sessions.AddProductAsync(s.Id, water.Id, 2);
        await _t.Sessions.CancelAsync(s.Id, "Started by mistake");
        Assert.Equal(water.Stock, (await _t.Product("Water 50cl")).Stock);
        var stats = await _t.Reports.GetDashboardStatsAsync(_t.Clock.Now);
        Assert.Equal(0m, stats.Revenue);
    }

    [Fact]
    public async Task Counter_sale_and_reports()
    {
        var cola = await _t.Product("Coca-Cola 33cl");
        var chips = await _t.Product("Chips");
        var pay = await _t.Sessions.CounterSaleAsync(new CounterSaleRequest([new CartLine(cola.Id, 1), new CartLine(chips.Id, 2)], PaymentMethod.Cash, 500m, null));
        Assert.Equal(350m, pay.TotalAmount);
        Assert.Equal(150m, pay.ChangeGiven);

        var st = await _t.Station("PS5 #01");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        _t.Clock.Now = _t.Clock.Now.AddHours(2);
        await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Card, 0, null));

        var stats = await _t.Reports.GetDashboardStatsAsync(_t.Clock.Now);
        Assert.Equal(950m, stats.Revenue);
        Assert.Equal(600m, stats.GamingRevenue);
        Assert.Equal(350m, stats.ProductRevenue);
        Assert.Equal(70m + 2 * 40m, stats.ProductProfit);
        Assert.Equal(1, stats.Sessions);

        var report = await _t.Reports.GetReportAsync(_t.Clock.Now.Date.AddDays(-6), _t.Clock.Now.Date.AddDays(1), false);
        Assert.Equal(7, report.PerDay.Count);
        Assert.Equal(950m, report.TotalRevenue);
        Assert.Equal("PS5 #01", report.PerStation[0].Name);
    }

    [Fact]
    public async Task Cash_payment_below_total_is_rejected_and_session_stays_open()
    {
        var st = await _t.Station("PS5 #01");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        _t.Clock.Now = _t.Clock.Now.AddHours(1);
        await Assert.ThrowsAsync<BusinessException>(() => _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 100m, null)));
        Assert.Single(await _t.Sessions.GetLiveSessionsAsync());
    }

    [Fact]
    public async Task Operator_cannot_change_prices()
    {
        _t.User.User = new UserDto(2, "operator", "Operator", UserRole.Operator, true, null);
        var st = await _t.Station("PS5 #01");
        await Assert.ThrowsAsync<BusinessException>(() => _t.Stations.SaveAsync(new SaveStationRequest(st.Id, st.Name, st.Number, st.StationTypeId,
            st.Brand, st.Model, st.ImagePath, 1m, st.Description, st.Location, st.ControllerCount, st.State, true)));
    }

    [Fact]
    public async Task Extra_controllers_raise_the_hourly_rate()
    {
        var ps5 = await _t.Station("PS5 #01"); // seeded: 300 DA/h, 2 included, max 4, +100/h each extra
        Assert.True(ps5.HasControllerPricing);
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(ps5.Id, null, SessionMode.Open, null, null, Controllers: 4));
        Assert.Equal(500m, s.HourlyRate);
        Assert.Equal(4, s.Controllers);

        _t.Clock.Now = _t.Clock.Now.AddHours(1);
        var pay = await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 0, null));
        Assert.Equal(500m, pay.GamingAmount);
    }

    [Fact]
    public async Task Adding_controllers_mid_session_only_affects_the_rest()
    {
        var ps5 = await _t.Station("PS5 #02");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(ps5.Id, null, SessionMode.Open, null, null));
        Assert.Equal(300m, s.HourlyRate);
        Assert.Equal(2, s.Controllers);

        _t.Clock.Now = _t.Clock.Now.AddMinutes(60);        // 1h with 2 controllers = 300
        s = await _t.Sessions.ChangeControllersAsync(s.Id, 4);
        Assert.Equal(500m, s.HourlyRate);
        _t.Clock.Now = _t.Clock.Now.AddMinutes(30);        // 30 min with 4 controllers = 250

        var pay = await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 0, null));
        Assert.Equal(550m, pay.GamingAmount);

        var receipt = await _t.Reports.GetReceiptAsync(s.Id);
        Assert.Contains("4 ctrl @500/h", receipt!.RateNote);
    }

    [Fact]
    public async Task Controller_change_keeps_budget_and_fixed_rules()
    {
        var ps5 = await _t.Station("PS5 #03");
        // Budget 600 at 300/h; after 1h (300 spent) switch to 4 controllers (500/h): 300 left lasts 36 min.
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(ps5.Id, null, SessionMode.FixedBudget, null, 600m));
        _t.Clock.Now = _t.Clock.Now.AddHours(1);
        s = await _t.Sessions.ChangeControllersAsync(s.Id, 4);
        Assert.Equal(TimeSpan.FromMinutes(36), s.RemainingTime(_t.Clock.Now));
        _t.Clock.Now = _t.Clock.Now.AddHours(2);
        Assert.Equal(600m, s.GamingCost(_t.Clock.Now)); // never above budget

        var xbox = await _t.Station("Xbox #01"); // 250/h +100 per extra
        var f = await _t.Sessions.StartAsync(new StartSessionRequest(xbox.Id, null, SessionMode.FixedDuration, 120, null));
        _t.Clock.Now = _t.Clock.Now.AddHours(1);
        f = await _t.Sessions.ChangeControllersAsync(f.Id, 3);
        // 1h at 250 + remaining 1h at 350 = 600
        Assert.Equal(600m, f.GamingCost(_t.Clock.Now));
    }

    [Fact]
    public async Task Controllers_outside_station_limits_are_refused()
    {
        var ps5 = await _t.Station("PS5 #04");
        await Assert.ThrowsAsync<BusinessException>(() => _t.Sessions.StartAsync(new StartSessionRequest(ps5.Id, null, SessionMode.Open, null, null, Controllers: 5)));
        var pc = await _t.Station("PC #01"); // no controller pricing
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(pc.Id, null, SessionMode.Open, null, null, Controllers: 4));
        Assert.Equal(250m, s.HourlyRate);
        await Assert.ThrowsAsync<BusinessException>(() => _t.Sessions.ChangeControllersAsync(s.Id, 2));
    }
}
