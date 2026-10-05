// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using NetFluss.Core;
using Xunit;

namespace NetFluss.Core.Tests;

public class UpdateSignatureTests
{
    private static readonly (string PrivateKey, string PublicKey) Keys = UpdateSignature.CreateKeyPair();

    private static readonly byte[] Sums = Encoding.ASCII.GetBytes(
        $"{new string('a', 64)}  NetFluss-Setup-1.0.0-x64.exe\r\n{new string('b', 64)}  NetFluss-Setup-1.0.0-arm64.exe\r\n");

    [Fact]
    public void ASignedList_Verifies()
        => Assert.True(UpdateSignature.Verify(Sums, UpdateSignature.Sign(Sums, Keys.PrivateKey), Keys.PublicKey));

    [Fact]
    public void AChangedList_DoesNotVerify()
    {
        var signature = UpdateSignature.Sign(Sums, Keys.PrivateKey);
        var swapped = (byte[])Sums.Clone();
        swapped[0] = (byte)'c';
        Assert.False(UpdateSignature.Verify(swapped, signature, Keys.PublicKey));
    }

    [Fact]
    public void AnotherKey_DoesNotVerify()
    {
        var other = UpdateSignature.CreateKeyPair();
        Assert.False(UpdateSignature.Verify(Sums, UpdateSignature.Sign(Sums, other.PrivateKey), Keys.PublicKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void MissingOrMalformed_DoesNotVerify(string? signature)
        => Assert.False(UpdateSignature.Verify(Sums, signature, Keys.PublicKey));

    [Fact]
    public void TrailingNewline_InTheSigFile_IsTolerated()
        => Assert.True(UpdateSignature.Verify(Sums, UpdateSignature.Sign(Sums, Keys.PrivateKey) + "\r\n", Keys.PublicKey));

    [Fact]
    public void TheCompiledInKey_IsAValidP256PublicKey()
    {
        // A signature by any other key must fail cleanly — not throw on a malformed constant.
        Assert.False(UpdateSignature.Verify(Sums, UpdateSignature.Sign(Sums, Keys.PrivateKey)));
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(UpdateSignature.PublicKey);
        Assert.Equal(256, key.KeySize);
    }
}
