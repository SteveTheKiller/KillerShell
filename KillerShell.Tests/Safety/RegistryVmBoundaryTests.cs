using System;
using System.Reflection;
using KillerShell.Tests.Services;
using Xunit;

namespace KillerShell.Tests.Safety;

public sealed class RegistryVmBoundaryTests
{
    [Theory]
    [InlineData(nameof(RegistryEditorLogicTests.RenameKey_PreservesProtectedPermissionsAndChildren))]
    [InlineData(nameof(RegistryEditorLogicTests.RenameKey_OntoAnExistingName_ChangesNothing))]
    [InlineData(nameof(RegistryEditorLogicTests.Renames_KeepUncommonValueTypes))]
    public void NativeRegistryTestsRequireVmAdmissionAtDiscovery(string methodName)
    {
        var method = typeof(RegistryEditorLogicTests).GetMethod(methodName)!;
        var fact = Assert.Single(method.GetCustomAttributes<FactAttribute>());

        Assert.IsType<DisposableVmFactAttribute>(fact);
        if (Environment.MachineName.Equals("SHREDDER", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KILLERSHELL_DISPOSABLE_VM")))
            Assert.False(string.IsNullOrWhiteSpace(fact.Skip));
    }

    [Theory]
    [InlineData("SHREDDER", "SHREDDER", "Microsoft Corporation", "Virtual Machine")]
    [InlineData("shredder", "SHREDDER", "Microsoft Corporation", "Virtual Machine")]
    [InlineData("KILLERLAB", null, "Microsoft Corporation", "Virtual Machine")]
    [InlineData("KILLERLAB", "", "Microsoft Corporation", "Virtual Machine")]
    [InlineData("KILLERLAB", "OTHER-VM", "Microsoft Corporation", "Virtual Machine")]
    [InlineData("KILLERLAB", "KILLERLAB", "To Be Filled By O.E.M.", "To Be Filled By O.E.M.")]
    [InlineData("KILLERLAB", "KILLERLAB", "Microsoft Corporation", "Surface")]
    [InlineData("KILLERLAB", "KILLERLAB", "Other", "Virtual Machine")]
    [InlineData("KILLERLAB", "KILLERLAB", null, "Virtual Machine")]
    [InlineData("KILLERLAB", "KILLERLAB", "Microsoft Corporation", null)]
    [InlineData("", "", "Microsoft Corporation", "Virtual Machine")]
    public void AdmissionRefusesHostsAndUnverifiedOrUnacknowledgedGuests(string machine,
        string? acknowledgment, string? manufacturer, string? product)
    {
        Assert.False(string.IsNullOrWhiteSpace(
            DisposableVmGuard.GetRefusalReason(machine, acknowledgment, manufacturer, product)));
    }

    [Theory]
    [InlineData("KILLERLAB", "KILLERLAB")]
    [InlineData("KillerLab", "killerlab")]
    public void AdmissionAllowsAnAcknowledgedHyperVGuest(string machine, string acknowledgment)
    {
        Assert.Null(DisposableVmGuard.GetRefusalReason(machine, acknowledgment,
            "Microsoft Corporation", "Virtual Machine"));
    }
}
