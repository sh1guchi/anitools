using Anitools.Core.Media;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Media;

public sealed class CachedMediaProbeTests
{
    [Fact]
    public async Task Reads_once_rereads_changed_file_and_does_not_remember_errors()
    {
        using var dir = new TempDir();
        var file = dir.File("a.mkv", "x");
        var inner = new CountingProbe();
        var cache = new CachedMediaProbe(inner);
        var ct = TestContext.Current.CancellationToken;

        await cache.ProbeAsync(file, ct);
        await cache.ProbeAsync(file, ct);
        await cache.IdentifyAsync(file, ct);
        Assert.Equal((1, 1), (inner.Probes, inner.Identifications));

        File.WriteAllText(file, "длиннее");
        await cache.ProbeAsync(file, ct);
        Assert.Equal(2, inner.Probes);

        inner.Fail = true;
        var missing = dir.Combine("b.mkv");
        await Assert.ThrowsAsync<MediaProbeException>(() => cache.ProbeAsync(missing, ct));
        inner.Fail = false;
        await cache.ProbeAsync(missing, ct);
        Assert.Equal(4, inner.Probes);

        cache.Clear();
        await cache.ProbeAsync(file, ct);
        Assert.Equal(5, inner.Probes);
    }

    private sealed class CountingProbe : IMediaProbe
    {
        public int Probes { get; private set; }

        public int Identifications { get; private set; }

        public bool Fail { get; set; }

        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Probes++;
            return Fail ? throw new MediaProbeException(path, "нет файла") : Task.FromResult(MediaProbe.ParseFfprobe("""{"streams":[]}"""));
        }

        public Task<MkvIdentification> IdentifyAsync(string path, CancellationToken cancellationToken = default)
        {
            Identifications++;
            return Task.FromResult(MkvIdentification.Empty);
        }
    }
}
