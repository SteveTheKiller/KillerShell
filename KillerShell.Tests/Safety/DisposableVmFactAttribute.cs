using System;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Safety;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DisposableVmFactAttribute : FactAttribute
{
    public DisposableVmFactAttribute() => Skip = DisposableVmGuard.GetSkipReason();
}

internal static class DisposableVmGuard
{
    internal const string AcknowledgmentVariable = "KILLERSHELL_DISPOSABLE_VM";

    internal static string? GetSkipReason()
    {
        try
        {
            string machineName = Environment.MachineName;
            string? acknowledgment = Environment.GetEnvironmentVariable(AcknowledgmentVariable);
            string? machineRefusal = GetMachineRefusal(machineName, acknowledgment);
            if (machineRefusal != null)
                return machineRefusal;

            // Read firmware identity. Hyper-V being enabled on a host does not make it a guest.
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false);
            return GetRefusalReason(machineName, acknowledgment,
                bios?.GetValue("SystemManufacturer") as string,
                bios?.GetValue("SystemProductName") as string);
        }
        catch (Exception)
        {
            return "VM-only registry test: the guest identity could not be verified.";
        }
    }

    internal static void RequireDisposableVm()
    {
        string? refusal = GetSkipReason();
        if (refusal != null)
            throw new InvalidOperationException(refusal);
    }

    internal static string? GetRefusalReason(string machineName, string? acknowledgment,
        string? manufacturer, string? productName)
    {
        string? machineRefusal = GetMachineRefusal(machineName, acknowledgment);
        if (machineRefusal != null)
            return machineRefusal;

        return string.Equals(manufacturer, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            && string.Equals(productName, "Virtual Machine", StringComparison.OrdinalIgnoreCase)
            ? null
            : "VM-only registry test: firmware must identify a Hyper-V guest.";
    }

    private static string? GetMachineRefusal(string machineName, string? acknowledgment)
    {
        if (string.Equals(machineName, "SHREDDER", StringComparison.OrdinalIgnoreCase))
            return "VM-only registry test: execution on SHREDDER is prohibited.";

        if (string.IsNullOrWhiteSpace(machineName) || string.IsNullOrWhiteSpace(acknowledgment)
            || !string.Equals(machineName, acknowledgment, StringComparison.OrdinalIgnoreCase))
            return "VM-only registry test: set KILLERSHELL_DISPOSABLE_VM to the actual machine name only inside an approved disposable guest.";

        return null;
    }
}
