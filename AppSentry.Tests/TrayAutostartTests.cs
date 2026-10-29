using System.Xml.Linq;
using AppSentry.Core.Service;
using Xunit;

namespace AppSentry.Tests;

public class TrayAutostartTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskStartsTheExeForEveryUserAtSignIn()
    {
        var task = XDocument.Parse(TrayAutostart.TaskXml(@"C:\Program Files\AppSentry\AppSentry.exe")).Root!;

        Assert.NotNull(task.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger"));
        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("S-1-5-32-545", principal.Element(Ns + "GroupId")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value); // never stopped after 3 days
        Assert.Equal("Parallel", settings.Element(Ns + "MultipleInstancesPolicy")!.Value); // one copy per signed-in user
        var exec = task.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(@"C:\Program Files\AppSentry\AppSentry.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal(TrayAutostart.Argument, exec.Element(Ns + "Arguments")!.Value);
    }

    [Fact]
    public void PathIsEscaped()
    {
        var task = XDocument.Parse(TrayAutostart.TaskXml(@"C:\A & B <x>\AppSentry.exe")).Root!;
        Assert.Equal(@"C:\A & B <x>\AppSentry.exe", task.Descendants(Ns + "Command").Single().Value);
    }
}
