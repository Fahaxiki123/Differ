using Differ.Core.External;
using Differ.Core.Files;

namespace Differ.Svn.Tests;

public sealed class SvnWorkingFileVerifierTests : IDisposable
{
    [Fact]
    public void MissingNativeProviderDegradesToSaveAsWithoutFailingTheComparison()
    {
        using var context = new MissingProviderContext();
        var assembly = context.LoadFromAssemblyPath(typeof(NativeWorkingCopyFileVerifier).Assembly.Location);
        var type = assembly.GetType(typeof(NativeWorkingCopyFileVerifier).FullName!)!;
        var instance = Activator.CreateInstance(type);
        var result = type.GetMethod(nameof(NativeWorkingCopyFileVerifier.IsVersionedFile))!.Invoke(instance, [Path.Combine(_directory, "file.lua")]);
        Assert.False((bool)result!);
    }

    private sealed class MissingProviderContext() : System.Runtime.Loader.AssemblyLoadContext(isCollectible: true), IDisposable
    {
        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
        {
            if (assemblyName.Name == "SharpSvn") throw new FileNotFoundException("Isolated missing provider test.");
            return null;
        }
        public void Dispose() => Unload();
    }
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Differ-source-" + Guid.NewGuid().ToString("N"));

    public SvnWorkingFileVerifierTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("tortoisesvn", "WC", true, true)]
    [InlineData("tortoisesvn", "WORKING", true, true)]
    [InlineData(null, "WC", true, false)]
    [InlineData("tortoisesvn", "42", true, false)]
    [InlineData("tortoisesvn", "WC", false, false)]
    public async Task C04I01RequiresProtocolRevisionAndPublicWorkingCopyValidation(string? source, string revision, bool versioned, bool expected)
    {
        var session = await CreateSessionAsync(source, revision);
        var actual = await new SvnWorkingFileVerifier(new FakeVerifier(versioned)).VerifyAsync(session);
        Assert.Equal(expected ? session.Request.MineFilePath : null, actual);
    }

    [Fact]
    public async Task C04DoesNotPromoteAChangedFileOrASnapshot()
    {
        var session = await CreateSessionAsync("tortoisesvn", "WC");
        var verifier = new SvnWorkingFileVerifier(new FakeVerifier(true));
        await File.WriteAllTextAsync(session.Request.MineFilePath, "external edit");
        Assert.Null(await verifier.VerifyAsync(session));
        session = session with { Request = session.Request with { MineFilePath = session.MineSnapshotPath } };
        Assert.Null(await verifier.VerifyAsync(session));
    }

    [Fact]
    public async Task C04NeverPromotesBaseEvenWhenItIsVersioned()
    {
        var session = await CreateSessionAsync("tortoisesvn", "WC");
        session = session with { Request = session.Request with { BaseFilePath = session.Request.MineFilePath } };
        Assert.Null(await new SvnWorkingFileVerifier(new FakeVerifier(true)).VerifyAsync(session));
    }

    private async Task<ExternalDiffSession> CreateSessionAsync(string? source, string revision)
    {
        var working = Path.Combine(_directory, "working.lua");
        await File.WriteAllTextAsync(working, "local value = 3");
        var snapshot = await FileSnapshot.CreateAsync(working);
        return new ExternalDiffSession
        {
            SessionId = Guid.NewGuid(),
            Request = new ExternalDiffRequest
            {
                BaseFilePath = Path.Combine(_directory, "original.lua"),
                MineFilePath = working,
                InvocationSource = source,
                MineRevision = revision
            },
            SessionDirectoryPath = Path.Combine(_directory, "session"),
            BaseSnapshotPath = Path.Combine(_directory, "session", "base.lua"),
            MineSnapshotPath = Path.Combine(_directory, "session", "mine.lua"),
            MineSha256 = snapshot.Sha256
        };
    }

    public void Dispose() => Directory.Delete(_directory, true);
    private sealed class FakeVerifier(bool versioned) : IWorkingCopyFileVerifier
    {
        public bool IsVersionedFile(string fullPath) => versioned;
    }
}

