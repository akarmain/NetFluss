// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public class BuildVersionTests
{
    [Theory]
    [InlineData("2.6.0-beta.1", true)]
    [InlineData("2.6.0-beta.1+8bb11ee", true)]
    [InlineData("2.6.0", false)]
    [InlineData("2.6.0+8bb11ee-dirty", false)]
    public void Prerelease_IsTheSuffixNotTheMetadata(string version, bool prerelease)
        => Assert.Equal(prerelease, BuildVersion.IsPrerelease(version));

    [Fact]
    public void TheCommitMetadata_IsDropped()
    {
        // The test assembly is built with the repository's Version, plus "+<commit>" from the SDK.
        var version = BuildVersion.Of(typeof(BuildVersionTests).Assembly);
        Assert.DoesNotContain('+', version);
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }
}
