namespace Differ.App.Services;

public enum IntegrationMaintenanceOperation
{
    None,
    Invalid,
    Install,
    Restore,
    Status
}

public static class IntegrationMaintenanceArguments
{
    public static IntegrationMaintenanceOperation Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var hasMaintenanceArgument = arguments.Any(argument =>
            argument.StartsWith("--install-integration", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--restore-integration", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--integration-status", StringComparison.OrdinalIgnoreCase));
        if (!hasMaintenanceArgument)
        {
            return IntegrationMaintenanceOperation.None;
        }

        if (arguments.Count != 1)
        {
            return IntegrationMaintenanceOperation.Invalid;
        }

        return arguments[0] switch
        {
            "--install-integration" => IntegrationMaintenanceOperation.Install,
            "--restore-integration" => IntegrationMaintenanceOperation.Restore,
            "--integration-status" => IntegrationMaintenanceOperation.Status,
            _ => IntegrationMaintenanceOperation.Invalid
        };
    }
}
