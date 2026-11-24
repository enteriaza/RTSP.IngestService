// Tests for exit code classification of terminating exceptions and final lifecycle states.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Hosting;

/// <summary>
/// Tests for <see cref="ExitCodes"/>.
/// </summary>
public sealed class ExitCodesTests
{
    /// <summary>A representative configuration failure.</summary>
    private static readonly OptionsValidationException ConfigurationFailure = new(Options.DefaultName, typeof(object), ["invalid"]);

    /// <summary>Verifies that options validation failures map to the configuration error code.</summary>
    [Fact]
    public void FromException_OptionsValidation_IsConfigurationError() =>
        Assert.Equal(ExitCodes.ConfigurationError, ExitCodes.FromException(ConfigurationFailure));

    /// <summary>
    /// Verifies that an aggregate of validation failures (as thrown by startup validation of several options types)
    /// is a configuration error, but an aggregate that also contains another failure is not.
    /// </summary>
    [Fact]
    public void FromException_Aggregate_ClassifiedByAllInnerExceptions()
    {
        Assert.Equal(ExitCodes.ConfigurationError, ExitCodes.FromException(new AggregateException(ConfigurationFailure, ConfigurationFailure)));
        Assert.Equal(ExitCodes.Failure, ExitCodes.FromException(new AggregateException(ConfigurationFailure, new InvalidOperationException())));
        Assert.Equal(ExitCodes.Failure, ExitCodes.FromException(new AggregateException()));
    }

    /// <summary>Verifies that other exceptions map to the general failure code.</summary>
    [Fact]
    public void FromException_Other_IsFailure() =>
        Assert.Equal(ExitCodes.Failure, ExitCodes.FromException(new TimeoutException()));

    /// <summary>Verifies that every non-failed final state maps to success.</summary>
    /// <param name="state">The final state.</param>
    [Theory]
    [InlineData(ApplicationState.NotStarted)]
    [InlineData(ApplicationState.Running)]
    [InlineData(ApplicationState.Stopped)]
    public void FromLifecycle_NotFailed_IsSuccess(ApplicationState state) =>
        Assert.Equal(ExitCodes.Success, ExitCodes.FromLifecycle(new StubApplicationLifecycle { State = state }));

    /// <summary>Verifies that a failed lifecycle maps through its recorded failure.</summary>
    [Fact]
    public void FromLifecycle_Failed_UsesRecordedFailure()
    {
        Assert.Equal(ExitCodes.ConfigurationError, ExitCodes.FromLifecycle(new StubApplicationLifecycle { State = ApplicationState.Failed, Failure = ConfigurationFailure }));
        Assert.Equal(ExitCodes.Failure, ExitCodes.FromLifecycle(new StubApplicationLifecycle { State = ApplicationState.Failed, Failure = new InvalidOperationException() }));
        Assert.Equal(ExitCodes.Failure, ExitCodes.FromLifecycle(new StubApplicationLifecycle { State = ApplicationState.Failed }));
    }
}
