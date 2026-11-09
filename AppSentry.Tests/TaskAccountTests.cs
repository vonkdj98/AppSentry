using System.Security.Principal;
using AppSentry.Core.Sources;

namespace AppSentry.Tests;

/// <summary>A task saved with a SID, then with the same account's name, is not a change of account.</summary>
public class TaskAccountTests
{
    [Fact]
    public void SidAndNameOfOneAccountMatch()
    {
        using var me = WindowsIdentity.GetCurrent();
        var name = me.Name; // DOMAIN\user
        var sid = me.User!.Value;

        Assert.True(TaskSource.SameAccount(sid, name));
        Assert.True(TaskSource.SameAccount(name, sid));
        Assert.True(TaskSource.SameAccount(sid, name.Split('\\')[^1])); // bare user name, as Firefox writes it
    }

    [Fact]
    public void DifferentAccountsDoNot()
    {
        using var me = WindowsIdentity.GetCurrent();
        Assert.False(TaskSource.SameAccount(me.User!.Value, "S-1-5-18")); // SYSTEM
        Assert.False(TaskSource.SameAccount(me.User!.Value, ""));
        Assert.False(TaskSource.SameAccount("no-such-account-xyz", "S-1-5-18"));
    }
}
