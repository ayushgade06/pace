using Pace.Claude;
using Xunit;

namespace Pace.Tests;

public class RefreshTests
{
    static UsageSnapshot Snap(double pct) => new([new UsageLimit("session", pct, null, null)], []);

    static UsageViewModel Vm(Func<CancellationToken, Task<UsageSnapshot>> fetch) =>
        new(fetch, () => "pro", new Settings());

    [Fact]
    public async Task ConcurrentRefreshesMakeOneRequest()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<UsageSnapshot>();
        var vm = Vm(_ => { Interlocked.Increment(ref calls); return gate.Task; });
        var a = vm.RefreshAsync();
        var b = vm.RefreshAsync(); // timer + button overlap
        var c = vm.RefreshAsync();
        Assert.True(vm.Loading);
        gate.SetResult(Snap(10));
        await Task.WhenAll(a, b, c);
        Assert.Equal(1, calls);
        Assert.False(vm.Loading);
        await vm.RefreshAsync(); // allowed again once finished
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailureKeepsLastGoodData()
    {
        var fail = false;
        var vm = Vm(_ => fail ? throw new UsageException("HTTP 500") : Task.FromResult(Snap(42)));
        await vm.RefreshAsync();
        var updated = vm.LastUpdated;
        Assert.Equal("42%", Assert.Single(vm.Limits).PercentText);
        Assert.Null(vm.Error);

        fail = true;
        await vm.RefreshAsync();
        Assert.Equal("HTTP 500", vm.Error);
        Assert.Equal("42%", Assert.Single(vm.Limits).PercentText);
        Assert.Equal(updated, vm.LastUpdated);
        Assert.EndsWith("⚠", vm.TrayText);

        fail = false;
        await vm.RefreshAsync();
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task UnexpectedExceptionBecomesErrorNotCrash()
    {
        var vm = Vm(_ => throw new InvalidOperationException("boom"));
        await vm.RefreshAsync();
        Assert.Contains("boom", vm.Error);
        Assert.False(vm.Loading);
    }

    [Fact]
    public async Task PopulatesPlanTokensAndTray()
    {
        var vm = Vm(_ => Task.FromResult(new UsageSnapshot(
            [new UsageLimit("session", 54.7, null, null), new UsageLimit("weekly_all", 8, null, null)], [])));
        await vm.RefreshAsync();
        Assert.Equal("Pro", vm.PlanText);
        Assert.Equal("Pace · session 54% · week 8%", vm.TrayText);
        Assert.Equal(54.7, vm.SessionPercent);
    }

}
