using System.Reflection;
using NetArchTest.Rules;
using SmartShop.Bootstrap;
using SmartShop.Contracts;

namespace SmartShop.ArchitectureTests;

/// <summary>
/// Enforces the modular-monolith rules from docs/03-architecture.md so that any module can later be
/// extracted into its own service.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly Assembly[] ModuleAssemblies =
        SmartShopHost.Modules.Select(m => m.GetType().Assembly).Distinct().ToArray();

    private static string ModuleNamespace(Assembly assembly) => assembly.GetName().Name!;

    public static TheoryData<string> Modules()
    {
        var data = new TheoryData<string>();
        foreach (var assembly in ModuleAssemblies) data.Add(ModuleNamespace(assembly));
        return data;
    }

    [Fact]
    public void All_modules_are_registered()
    {
        ModuleAssemblies.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Modules_do_not_reference_other_modules(string module)
    {
        var assembly = ModuleAssemblies.Single(a => ModuleNamespace(a) == module);
        var others = ModuleAssemblies.Where(a => a != assembly).Select(ModuleNamespace).ToArray();
        if (others.Length == 0) return;

        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(others).GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{module} must talk to other modules only through SmartShop.Contracts. Offending types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Domain_does_not_depend_on_infrastructure_or_frameworks(string module)
    {
        var assembly = ModuleAssemblies.Single(a => ModuleNamespace(a) == module);

        var result = Types.InAssembly(assembly)
            .That().ResideInNamespace($"{module}.Domain")
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Wolverine", "SmartShop.Infrastructure")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("Domain types must stay persistence- and framework-agnostic: " +
                                         string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Contracts_do_not_depend_on_modules_or_infrastructure()
    {
        var result = Types.InAssembly(typeof(IIntegrationEvent).Assembly)
            .ShouldNot().HaveDependencyOnAny([.. ModuleAssemblies.Select(ModuleNamespace), "SmartShop.Infrastructure", "Microsoft.EntityFrameworkCore"])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Integration_events_are_immutable_records_named_in_past_tense_or_as_commands()
    {
        var events = typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => typeof(IAsyncMessage).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

        foreach (var type in events)
        {
            type.GetMethod("<Clone>$").ShouldNotBeNull($"{type.Name} should be a record");
            type.IsSealed.ShouldBeTrue($"{type.Name} should be sealed");
        }
    }
}

public class HandlerConventionTests
{
    /// <summary>Wolverine only discovers classes whose name ends with "Handler" (or "Consumer").</summary>
    [Fact]
    public void Classes_in_handler_namespaces_follow_the_wolverine_naming_convention()
    {
        var offenders = SmartShopHost.Modules.Select(m => m.GetType().Assembly).Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsPublic && t.Namespace?.EndsWith(".Handlers", StringComparison.Ordinal) == true)
            .Where(t => t.GetMethods().Any(m => m.Name is "Handle" or "HandleAsync" && m.IsStatic))
            .Where(t => !t.Name.EndsWith("Handler", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();
    }
}
