using RecruIT.List.Services.Navigation;

namespace RecruIT.List.Tests;

public sealed class BreadcrumbStateTests
{
    [Fact]
    public void StartsEmpty() => Assert.Empty(new BreadcrumbState().Items);

    [Fact]
    public void Set_ReplacesTheTrailAndNotifiesSubscribers()
    {
        var state = new BreadcrumbState();
        var notifications = 0;
        state.Changed += () => notifications++;

        state.Set(new BreadcrumbItem("All trials", "/"), new BreadcrumbItem("AMICA"));
        state.Set(new BreadcrumbItem("Notifications"));

        Assert.Equal([new BreadcrumbItem("Notifications")], state.Items);
        Assert.Equal(2, notifications);
    }
}
