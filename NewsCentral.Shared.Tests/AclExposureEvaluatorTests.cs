using System.Security.AccessControl;
using System.Security.Principal;
using NewsCentral.Security;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>The shared evaluator with caller-supplied masks (the NewsService write mask is covered by ImagePublisherAclTests).</summary>
public sealed class AclExposureEvaluatorTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private const FileSystemRights ReadRights = FileSystemRights.ReadData;

    private static AclRuleView Allow(SecurityIdentifier s, FileSystemRights r) => new(s, (int)r, true);
    private static AclRuleView Deny(SecurityIdentifier s, FileSystemRights r) => new(s, (int)r, false);

    [Fact]
    public void ReadMask_BroadReadIsReported_AdminReadIsNot()
    {
        var mask = AclExposureEvaluator.WithGenericRead(ReadRights);

        Assert.Null(AclExposureEvaluator.FindExposure([Allow(Admins, FileSystemRights.FullControl)], mask, ReadRights));
        var finding = AclExposureEvaluator.FindExposure([Allow(Users, FileSystemRights.Read)], mask, ReadRights);
        Assert.Equal("Users", finding?.Identity);
    }

    [Fact]
    public void GenericRead_CountsForAReadMask_NotForAWriteMask()
    {
        var generic = new AclRuleView(Users, unchecked((int)0x80000000), true);

        Assert.NotNull(AclExposureEvaluator.FindExposure([generic], AclExposureEvaluator.WithGenericRead(ReadRights), ReadRights));
        Assert.Null(AclExposureEvaluator.FindExposure([generic],
            AclExposureEvaluator.WithGenericWrite(FileSystemRights.WriteData), FileSystemRights.WriteData));
    }

    [Fact]
    public void Deny_CancelsMatchingAllow_ButIsNotAnExposureAlone()
    {
        var mask = AclExposureEvaluator.WithGenericRead(ReadRights);

        Assert.Null(AclExposureEvaluator.FindExposure([Allow(Users, ReadRights), Deny(Users, ReadRights)], mask, ReadRights));
        Assert.Null(AclExposureEvaluator.FindExposure([Deny(Users, ReadRights)], mask, ReadRights));
    }
}
