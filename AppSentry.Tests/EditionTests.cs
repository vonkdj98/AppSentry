using AppSentry.Core.Backend;
using AppSentry.Core.Editions;
using AppSentry.Core.Engine;

namespace AppSentry.Tests;

/// <summary>The open-source build has no edition: the hooks must be inert.</summary>
public class EditionTests
{
    [Fact]
    public async Task Without_an_edition_there_are_no_editions_and_edition_ops_are_unknown()
    {
        using var dir = new TempDir();
        using var engine = new MonitorEngine(new EngineOptions { DataDir = dir.Path });

        Assert.Empty(engine.Editions);
        var response = engine.HandleEditionOp(new EditionRequest("x.status", null, CallerIsAdmin: true, "test"));
        Assert.False(response.Ok);
        Assert.Contains("Unknown operation", response.Error);

        using var backend = new LocalBackend(dir.Path);
        Assert.Empty(backend.Editions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CallEditionAsync("x.status"));
    }
}
