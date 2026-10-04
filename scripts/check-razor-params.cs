#:package MudBlazor
// Fails when a .razor file passes a parameter that a MudBlazor component does not have (Blazor only detects this at runtime).
// Usage: dotnet run scripts/check-razor-params.cs -- src/Clients/SmartShop.UI
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

var asm = typeof(MudBlazor.MudButton).Assembly;
var types = asm.GetExportedTypes().Where(t => t.Name.StartsWith("Mud")).GroupBy(t => t.Name.Split('`')[0]).ToDictionary(g => g.Key, g => g.First());
var problems = 0;
foreach (var file in Directory.GetFiles(args[0], "*.razor", SearchOption.AllDirectories))
{
    var text = File.ReadAllText(file);
    foreach (Match tag in Regex.Matches(text, @"<(Mud[A-Za-z]+)\b((?:[^>""]|""[^""]*"")*)/?>"))
    {
        if (!types.TryGetValue(tag.Groups[1].Value, out var type)) { Console.WriteLine($"UNKNOWN COMPONENT {tag.Groups[1].Value} in {Path.GetFileName(file)}"); problems++; continue; }
        var parameters = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<ParameterAttribute>() != null || p.GetCustomAttribute<CascadingParameterAttribute>() != null)
            .Select(p => p.Name).ToHashSet();
        var captures = type.GetProperties().Any(p => p.GetCustomAttribute<ParameterAttribute>()?.CaptureUnmatchedValues == true);
        foreach (Match a in Regex.Matches(tag.Groups[2].Value, @"(?:^|\s)@?(?:bind-)?([A-Z][A-Za-z0-9]*)(?::[A-Za-z]+)?\s*="))
        {
            var name = a.Groups[1].Value;
            if (name == "T" || parameters.Contains(name)) continue;
            Console.WriteLine($"{Path.GetFileName(file)}: <{type.Name.Split('`')[0]} {name}> {(captures ? "(would be splatted as HTML attribute)" : "(RUNTIME ERROR)")}");
            problems++;
        }
    }
}
Console.WriteLine($"problems: {problems}");
return problems == 0 ? 0 : 1;
