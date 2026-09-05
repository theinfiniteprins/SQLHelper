using SqlHelper.Core.Credentials;

namespace SqlHelper.Core.Tests.Credentials;

public sealed class MasterKeyTests
{
    [Fact]
    public void Empty_passphrase_yields_no_entropy()
    {
        Assert.Empty(MasterKey.DeriveEntropy(ReadOnlySpan<char>.Empty));
        Assert.Empty(MasterKey.DeriveEntropy(""));
    }

    [Fact]
    public void Same_passphrase_is_deterministic_and_32_bytes()
    {
        byte[] first = MasterKey.DeriveEntropy("s3cret-passphrase");
        byte[] second = MasterKey.DeriveEntropy("s3cret-passphrase");

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_passphrases_diverge()
    {
        Assert.NotEqual(
            MasterKey.DeriveEntropy("passphrase-one"),
            MasterKey.DeriveEntropy("passphrase-two"));
    }
}
