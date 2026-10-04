using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Enums;

namespace GamingCenter.Tests;

public class ExpenseServiceTests : IDisposable
{
    private readonly TestDb _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task New_tv_is_subtracted_from_income_in_the_report()
    {
        var st = await _t.Station("PS5 #01");
        var s = await _t.Sessions.StartAsync(new StartSessionRequest(st.Id, null, SessionMode.Open, null, null));
        _t.Clock.Now = _t.Clock.Now.AddHours(2);
        await _t.Sessions.CompleteAsync(new CompleteSessionRequest(s.Id, _t.Clock.Now, PaymentMethod.Cash, 600m, null));

        var tv = await _t.Expenses.SaveAsync(new SaveExpenseRequest(null, _t.Clock.Now.Date, "Samsung TV 55\"", ExpenseCategory.Equipment, 450m, "For PS5 #01"));
        Assert.Equal(_t.Clock.Now, tv.Date);
        Assert.Equal("Administrator", tv.User);
        await _t.Expenses.SaveAsync(new SaveExpenseRequest(null, _t.Clock.Now.Date.AddDays(-1), "Electricity bill", ExpenseCategory.Electricity, 100m, null));

        var day = _t.Clock.Now.Date;
        var stats = await _t.Reports.GetDashboardStatsAsync(day);
        Assert.Equal(450m, stats.Expenses);
        Assert.Equal(1, stats.ExpenseCount);

        var report = await _t.Reports.GetReportAsync(day, day.AddDays(1), false);
        Assert.Equal(600m, report.TotalRevenue);
        Assert.Equal(450m, report.Expenses);
        Assert.Equal(150m, report.NetProfit);
        Assert.Equal(450m, report.PerDay.Sum(b => b.Expenses));
        Assert.Equal("Equipment", Assert.Single(report.ExpensesByCategory!).Name);

        var week = await _t.Reports.GetReportAsync(day.AddDays(-6), day.AddDays(1), false);
        Assert.Equal(550m, week.Expenses);
        Assert.Equal(50m, week.NetProfit);
        Assert.Equal(-100m, week.PerDay.Single(b => b.Day == day.AddDays(-1)).Net);

        var list = await _t.Expenses.GetAsync(day.AddDays(-6), day.AddDays(1));
        Assert.Equal(["Samsung TV 55\"", "Electricity bill"], list.Select(e => e.Description));
    }

    [Fact]
    public async Task Expense_can_be_edited_and_deleted()
    {
        var e = await _t.Expenses.SaveAsync(new SaveExpenseRequest(null, _t.Clock.Now.Date, "Controller", ExpenseCategory.Equipment, 80m, null));
        var edited = await _t.Expenses.SaveAsync(new SaveExpenseRequest(e.Id, e.Date, "DualSense controller", ExpenseCategory.Equipment, 95m, "Black"));
        Assert.Equal(e.Date, edited.Date);
        Assert.Equal(95m, edited.Amount);
        Assert.Equal("Black", edited.Note);

        await _t.Expenses.DeleteAsync(e.Id);
        Assert.Empty(await _t.Expenses.GetAsync(_t.Clock.Now.Date, _t.Clock.Now.Date.AddDays(1)));
    }

    [Fact]
    public async Task Invalid_or_non_admin_expenses_are_refused()
    {
        var today = _t.Clock.Now.Date;
        await Assert.ThrowsAsync<BusinessException>(() => _t.Expenses.SaveAsync(new SaveExpenseRequest(null, today, "TV", ExpenseCategory.Equipment, 0m, null)));
        await Assert.ThrowsAsync<BusinessException>(() => _t.Expenses.SaveAsync(new SaveExpenseRequest(null, today, " ", ExpenseCategory.Equipment, 10m, null)));
        await Assert.ThrowsAsync<BusinessException>(() => _t.Expenses.SaveAsync(new SaveExpenseRequest(null, today.AddDays(1), "TV", ExpenseCategory.Equipment, 10m, null)));

        _t.User.User = new UserDto(2, "operator", "Operator", UserRole.Operator, true, null);
        await Assert.ThrowsAsync<BusinessException>(() => _t.Expenses.SaveAsync(new SaveExpenseRequest(null, today, "TV", ExpenseCategory.Equipment, 10m, null)));
        await Assert.ThrowsAsync<BusinessException>(() => _t.Expenses.GetAsync(today, today.AddDays(1)));
    }
}
