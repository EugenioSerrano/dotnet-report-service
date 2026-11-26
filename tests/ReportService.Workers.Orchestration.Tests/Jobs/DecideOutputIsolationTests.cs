using System.Reflection;
using System.Runtime.CompilerServices;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The per-output isolation verdict as a pure function (US-008.BOLT-002, SPEC-261125-1224 Phase
/// D.0 / reviewer finding M3): <see cref="ReportJobWorkflow.DecideOutputIsolation"/>. The whole
/// decision is exercised hermetically — no Temporal worker — because the VB2 extraction made it a
/// pure function of the output id, the child's outcome and the output's own code mapping.
///
/// The oracle is the D.0 / M3 intent, NOT the helper body: an UNEXPECTED child fault — a
/// <see cref="ChildWorkflowFailureException"/> OR an <see cref="ActivityFailureException"/> — fails
/// ONLY that one output (<c>ShouldFailOutput == true</c>, the output id echoed, the code mapped
/// from the fault's inner via the injected <c>codeOf</c>) and NEVER fails the whole job, so a
/// sibling's success still reaches finalisation; any other outcome — a different exception type, or
/// no fault (the child ended its own output normally) — is PASS-THROUGH
/// (<c>ShouldFailOutput == false</c>, <c>Code == null</c>) so the fault, if any, propagates
/// normally. A fake <c>codeOf</c> is injected and its argument asserted.
/// </summary>
public sealed class DecideOutputIsolationTests
{
    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    [Fact]
    public void AnUnexpectedChildWorkflowFaultFailsOnlyThatOutputWithItsMappedCode()
    {
        var inner = new InvalidOperationException("boom");
        var fault = ChildWorkflowFault(inner);
        Exception? seen = null;
        string CodeOf(Exception? e)
        {
            seen = e;
            return "MAPPED_CODE";
        }

        var decision = ReportJobWorkflow.DecideOutputIsolation(OutputId, fault, CodeOf);

        Assert.True(decision.ShouldFailOutput, "an unexpected child-workflow fault fails that output");
        Assert.Equal(OutputId, decision.OutputId);
        Assert.Equal("MAPPED_CODE", decision.Code);
        Assert.Same(inner, seen);
    }

    [Fact]
    public void AnUnexpectedActivityFaultFailsOnlyThatOutputWithItsMappedCode()
    {
        var inner = new TimeoutException("slow");
        var fault = ActivityFault(inner);
        Exception? seen = null;
        string CodeOf(Exception? e)
        {
            seen = e;
            return "EXECUTION_FAILED";
        }

        var decision = ReportJobWorkflow.DecideOutputIsolation(OutputId, fault, CodeOf);

        Assert.True(decision.ShouldFailOutput, "an unexpected activity fault fails that output");
        Assert.Equal(OutputId, decision.OutputId);
        Assert.Equal("EXECUTION_FAILED", decision.Code);
        Assert.Same(inner, seen);
    }

    [Fact]
    public void NoChildFaultIsPassThroughAndNeverFailsTheOutput()
    {
        var mapped = false;

        var decision = ReportJobWorkflow.DecideOutputIsolation(OutputId, null, _ =>
        {
            mapped = true;
            return "SHOULD_NOT_BE_CALLED";
        });

        // The child ended its own output normally (no fault): nothing is failed, no code is carried,
        // and the output id is still echoed. codeOf is never consulted.
        Assert.False(decision.ShouldFailOutput);
        Assert.Null(decision.Code);
        Assert.Equal(OutputId, decision.OutputId);
        Assert.False(mapped, "a pass-through verdict never maps a code");
    }

    [Fact]
    public void AnUnrelatedExceptionTypeIsPassThroughAndNeverFailsTheOutput()
    {
        var mapped = false;

        // A fault that is neither a ChildWorkflowFailureException nor an ActivityFailureException is
        // not an isolated-output failure: it propagates normally (pass-through).
        var decision = ReportJobWorkflow.DecideOutputIsolation(
            OutputId,
            new InvalidOperationException("some other fault"),
            _ =>
            {
                mapped = true;
                return "SHOULD_NOT_BE_CALLED";
            });

        Assert.False(decision.ShouldFailOutput);
        Assert.Null(decision.Code);
        Assert.Equal(OutputId, decision.OutputId);
        Assert.False(mapped, "a pass-through verdict never maps a code");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingOutputIdIsRejected(string? outputId)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ReportJobWorkflow.DecideOutputIsolation(outputId!, null, _ => "X"));
    }

    [Fact]
    public void AMissingCodeMappingIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ReportJobWorkflow.DecideOutputIsolation(OutputId, ActivityFault(new TimeoutException()), null!));
    }

    // Temporal's ChildWorkflowFailureException / ActivityFailureException expose no public
    // constructor, so a hermetic unit test builds an uninitialised instance of the exact type and
    // sets its InnerException — the only member DecideOutputIsolation reads from the fault.
    private static ChildWorkflowFailureException ChildWorkflowFault(Exception inner) =>
        (ChildWorkflowFailureException)WithInner(typeof(ChildWorkflowFailureException), inner);

    private static ActivityFailureException ActivityFault(Exception inner) =>
        (ActivityFailureException)WithInner(typeof(ActivityFailureException), inner);

    private static Exception WithInner(Type type, Exception inner)
    {
        var instance = (Exception)RuntimeHelpers.GetUninitializedObject(type);
        typeof(Exception)
            .GetField("_innerException", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(instance, inner);
        return instance;
    }
}
