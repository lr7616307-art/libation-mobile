using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class ExternalSignInFlowTests
{
    private static readonly Uri CallbackUri = new("libation://signin/return");

    [Fact]
    public void ValidCallback_CompletesFlow()
    {
        var flow = CreateFlow();

        var result = flow.AcceptCallback("libation://signin/return?state=opaque-state&code=temporary");

        Assert.Equal(ExternalSignInFlowStatus.Completed, result);
        Assert.Equal(ExternalSignInFlowStatus.Completed, flow.Status);
    }

    [Fact]
    public void WrongState_InvalidatesFlow()
    {
        var flow = CreateFlow();

        Assert.Equal(ExternalSignInFlowStatus.Invalid,
            flow.AcceptCallback("libation://signin/return?state=wrong"));
    }

    [Fact]
    public void WrongHost_InvalidatesFlow()
    {
        var flow = CreateFlow();

        Assert.Equal(ExternalSignInFlowStatus.Invalid,
            flow.AcceptCallback("libation://attacker/return?state=opaque-state"));
    }

    [Fact]
    public void WrongScheme_InvalidatesFlow()
    {
        var flow = CreateFlow();

        Assert.Equal(ExternalSignInFlowStatus.Invalid,
            flow.AcceptCallback("https://signin/return?state=opaque-state"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a uri")]
    [InlineData("libation://signin/return?state=%")]
    public void MalformedCallback_IsRejected(string? callback)
    {
        var flow = CreateFlow();

        Assert.Equal(ExternalSignInFlowStatus.Invalid, flow.AcceptCallback(callback));
    }

    [Fact]
    public void ReplayedCallback_IsRejected()
    {
        var flow = CreateFlow();
        const string callback = "libation://signin/return?state=opaque-state";

        Assert.Equal(ExternalSignInFlowStatus.Completed, flow.AcceptCallback(callback));
        Assert.Equal(ExternalSignInFlowStatus.Invalid, flow.AcceptCallback(callback));
        Assert.Equal(ExternalSignInFlowStatus.Completed, flow.Status);
    }

    [Fact]
    public void ExpiredPendingFlow_RejectsCallback()
    {
        var now = DateTimeOffset.UtcNow;
        var challenge = new ExternalSignInChallenge(
            "challenge", new Uri("https://example.invalid/login?state=opaque-state"),
            now.AddMinutes(5));
        var flow = new ExternalSignInFlow(challenge, CallbackUri, () => now);

        now = now.AddMinutes(6);

        Assert.Equal(ExternalSignInFlowStatus.Expired,
            flow.AcceptCallback("libation://signin/return?state=opaque-state"));
    }

    [Fact]
    public void Cancellation_EndsPendingFlowAndAllowsCleanRecovery()
    {
        var flow = CreateFlow();

        Assert.Equal(ExternalSignInFlowStatus.Cancelled, flow.Cancel());
        Assert.Equal(ExternalSignInFlowStatus.Cancelled,
            flow.AcceptCallback("libation://signin/return?state=opaque-state"));

        var nextAttempt = CreateFlow();
        Assert.Equal(ExternalSignInFlowStatus.Pending, nextAttempt.Status);
    }

    [Fact]
    public void AuthorizationUriWithoutUniqueState_IsRejected()
    {
        var challenge = new ExternalSignInChallenge(
            "challenge", new Uri("https://example.invalid/login?state=one&state=two"),
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Throws<ArgumentException>(() => new ExternalSignInFlow(challenge, CallbackUri));
    }

    private static ExternalSignInFlow CreateFlow()
    {
        var now = DateTimeOffset.UtcNow;
        var challenge = new ExternalSignInChallenge(
            "challenge", new Uri("https://example.invalid/login?state=opaque-state"),
            now.AddMinutes(5));
        return new ExternalSignInFlow(challenge, CallbackUri, () => now);
    }
}
