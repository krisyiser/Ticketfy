using Ticketfy.Core.Configuration;

namespace Ticketfy.Desktop.Tests;

public sealed class InternalTestingProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ticketfy-installer-profile-" + Guid.NewGuid().ToString("N"));
    public InternalTestingProfileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UnmarkedPackageRetainsItsNormalDefaultWithoutOpeningIt()
    {
        var profile = InternalTestingProfile.ResolveDefault(_root, _root);
        Assert.False(profile.IsInternalTesting);
        Assert.Equal(Path.Combine(_root, "Ticketfy"), profile.Directory);
        Assert.False(Directory.Exists(profile.Directory));
    }

    [Fact]
    public void InstalledEditionDefaultsToIsolatedDataEvenWithoutShortcutArguments()
    {
        File.WriteAllText(Path.Combine(_root, InternalTestingProfile.MarkerFileName), InternalTestingProfile.MarkerContents);
        var profile = InternalTestingProfile.ResolveDefault(_root, _root);
        Assert.True(profile.IsInternalTesting);
        Assert.Equal(Path.Combine(_root, "Ticketfy.InternalTesting", "Data"), profile.Directory);
        InternalTestingProfile.ValidateDirectory(profile.Directory, _root);
        InternalTestingProfile.ValidateDirectory(Path.Combine(_root, "Ticketfy.InternalTesting", "Restored"), _root);
        Assert.False(Directory.Exists(profile.Directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown-version")]
    [InlineData("Ticketfy.InternalTesting.v1\noperational-directory")]
    public void CorruptMarkerStopsStartupInsteadOfFallingBackToOperationalData(string contents)
    {
        File.WriteAllText(Path.Combine(_root, InternalTestingProfile.MarkerFileName), contents);
        Assert.Throws<InvalidOperationException>(() => InternalTestingProfile.ResolveDefault(_root, _root));
        Assert.False(Directory.Exists(Path.Combine(_root, "Ticketfy")));
    }

    [Fact]
    public void OversizedMarkerIsRejectedBeforeReadingItsContents()
    {
        File.WriteAllText(Path.Combine(_root, InternalTestingProfile.MarkerFileName), new string('x', 129));
        Assert.Throws<InvalidOperationException>(() => InternalTestingProfile.ResolveDefault(_root, _root));
    }

    [Fact]
    public void OverrideCannotSelectOperationalSiblingRelativeTraversalOrFile()
    {
        foreach (string path in new[] { ".", Path.Combine(_root, "Ticketfy"), Path.Combine(_root, "Ticketfy.InternalTesting2", "Data"),
                     Path.Combine(_root, "Ticketfy.InternalTesting", "..", "Ticketfy"), Path.Combine(_root, "Ticketfy.InternalTesting") })
            Assert.Throws<ArgumentException>(() => InternalTestingProfile.ValidateDirectory(path, _root));
        string file = Path.Combine(_root, "Ticketfy.InternalTesting", "file");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "synthetic");
        Assert.Throws<ArgumentException>(() => InternalTestingProfile.ValidateDirectory(file, _root));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
