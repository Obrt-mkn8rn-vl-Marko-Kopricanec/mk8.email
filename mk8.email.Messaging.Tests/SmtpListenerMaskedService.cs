using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Messaging.Tests;

// The public virtual inspection property is not the service's shutdown authority.
internal sealed class SmtpListenerMaskedService(IServiceScopeFactory scopes, EnvironmentConfig environment,
    ILogger<SmtpServerService> logger, IGatewayTrafficJournal journal) : SmtpServerService(scopes, environment, logger, journal)
{
    // This deliberately absent nullable inspection value is the counterexample:
    // shutdown must still own the real, separately retained base execution task.
#pragma warning disable RCS1210, VSTHRD114
    public override Task? ExecuteTask => null;
#pragma warning restore RCS1210, VSTHRD114
    public Task? ActualCompleting => base.ExecuteTask;
}
