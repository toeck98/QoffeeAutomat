using System.Reflection;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Tests.Platform;

/// <summary>
/// "No type in Core references a Windows or Linux API" (PLAN.md T204): Core is the platform-neutral
/// layer, and everything operating-system specific lives behind the interfaces of
/// <c>QoffeeAutomat.Core.Platform</c>.
/// </summary>
[Trait("Category", "Platform")]
public sealed class CorePlatformNeutralityTests
{
    private static readonly Assembly CoreAssembly = typeof(IMetricProvider).Assembly;

    private static readonly string[] ForbiddenAssemblyFragments =
    [
        "Win32",
        "Windows",
        "ServiceProcess",
        "EventLog",
        "PerformanceCounter",
        "System.Management",
        "System.DirectoryServices",
        "Posix",
        "Tmds",
        "Mono.Unix",
    ];

    [Fact]
    public void Core_references_no_assembly_that_belongs_to_one_operating_system()
    {
        string[] references =
        [
            .. CoreAssembly.GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .Where(name => !string.Equals(name, "Microsoft.Win32.Primitives", StringComparison.OrdinalIgnoreCase)),
        ];

        foreach (string fragment in ForbiddenAssemblyFragments)
        {
            Assert.DoesNotContain(references, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Core_declares_no_native_interop_methods()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        MethodInfo[] pinvokes = [.. CoreAssembly.GetTypes()
            .SelectMany(type => type.GetMethods(all))
            .Where(method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0)];

        Assert.Empty(pinvokes);
    }

    [Fact]
    public void Core_does_not_reference_the_platform_projects()
    {
        string[] references =
        [
            .. CoreAssembly.GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .Where(name => !string.Equals(name, "Microsoft.Win32.Primitives", StringComparison.OrdinalIgnoreCase)),
        ];

        Assert.DoesNotContain("QoffeeAutomat.Platform.Windows", references);
        Assert.DoesNotContain("QoffeeAutomat.Platform.Simulated", references);
        Assert.DoesNotContain("QoffeeAutomat.Agent", references);
        Assert.Contains("QoffeeAutomat.Contracts", references);
    }

    [Fact]
    public void The_platform_interfaces_are_public_and_live_in_the_platform_namespace()
    {
        Type[] expected =
        [
            typeof(IMetricProvider),
            typeof(IBuiltinActionProvider),
            typeof(ISessionBridge),
            typeof(IPowerEvents),
            typeof(IServiceAdvertiser),
            typeof(IElevatedLauncher),
            typeof(IPlatformInfo),
        ];

        Assert.All(expected, type =>
        {
            Assert.True(type.IsInterface);
            Assert.True(type.IsPublic);
            Assert.Equal("QoffeeAutomat.Core.Platform", type.Namespace);
        });
    }
}
