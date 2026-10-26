using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>The long code of a sign-in link: read the way the service reads it, with the same check (cloud/src/formats.ts).</summary>
public sealed class LinkCodeTests
{
    // Made by the Worker's linkCodeCheck (cloud/test/formats.test.ts has the same two).
    [Theory]
    [InlineData("K7QM3XWDR9FAT2HN8VCJP4GEM2SB12A0")]
    [InlineData("0000000000000000000000000000GMMR")]
    public void TheChecksMatchTheServices(string code)
    {
        Assert.True(LinkCode.Checks(code));
        Assert.Equal(code, LinkCode.Read(code));
    }

    [Theory]
    [InlineData("K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-12A0")]
    [InlineData(" k7qm 3xwd r9fa t2hn 8vcj p4ge m2sb 12a0 ")]
    [InlineData("K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-I2AO")] // I read as 1, O as 0
    [InlineData("K7QM3XWD\r\nR9FAT2HN\r\n8VCJP4GE\r\nM2SB12A0")]
    public void PastedOrTypedItIsStillTheCode(string typed) => Assert.Equal("K7QM3XWDR9FAT2HN8VCJP4GEM2SB12A0", LinkCode.Read(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-12A")] // cut short
    [InlineData("K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-12A01")] // one too many
    [InlineData("K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-12AU")] // U is never in a code
    [InlineData("K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-12A1")] // the check doesn't add up
    [InlineData("A7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-12A0")] // one character changed
    public void ATypoIsNotACode(string? typed) => Assert.Null(LinkCode.Read(typed));
}
