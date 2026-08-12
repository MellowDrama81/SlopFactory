using Mellow.SlopFactory.Gui.Services;
using Xunit;

namespace Mellow.SlopFactory.Tests;

public sealed class AppLifecycleStateTests
{
    [Fact]
    public void StartsInTheForeground()
    {
        var state = new AppLifecycleState();
        Assert.True(state.IsForeground);
    }

    [Fact]
    public void SetForegroundTogglesStateAndRaisesChangedOnlyOnAnActualTransition()
    {
        var state = new AppLifecycleState();
        var changeCount = 0;
        state.Changed += (_, _) => changeCount++;

        state.SetForeground(true);
        Assert.Equal(0, changeCount);
        Assert.True(state.IsForeground);

        state.SetForeground(false);
        Assert.Equal(1, changeCount);
        Assert.False(state.IsForeground);

        state.SetForeground(false);
        Assert.Equal(1, changeCount);

        state.SetForeground(true);
        Assert.Equal(2, changeCount);
        Assert.True(state.IsForeground);
    }
}
