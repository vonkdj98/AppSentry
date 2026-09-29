using AppSentry.Core.Backend;
using AppSentry.Core.Editions;
using AppSentry.Core.Engine;

namespace AppSentry.Tests;

/// <summary>The edition hooks: inert in the open-source build, and unknown ops are refused in any build.</summary>
public class EditionTests
{
    [Fact]
    public async Task Ops_no_edition_owns_are_reported_as_unknown()
    {
        using var dir = new TempDir();
        using var engine = new MonitorEngine(new EngineOptions { DataDir = dir.Path });

        var response = engine.HandleEditionOp(new EditionRequest("x.nobody", null, CallerIsAdmin: true, "test"));
        Assert.False(response.Ok);
        Assert.Contains("Unknown operation", response.Error);

        using var backend = new LocalBackend(dir.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CallEditionAsync("x.nobody"));
    }

#if !APPSENTRY_EDITION
    [Fact]
    public void The_open_source_build_has_no_editions()
    {
        using var dir = new TempDir();
        using var engine = new MonitorEngine(new EngineOptions { DataDir = dir.Path });
        Assert.Empty(engine.Editions);
    }
#endif
}
