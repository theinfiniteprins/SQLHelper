using System.IO;
using System.Text.RegularExpressions;

namespace SqlHelper.App.Tests;

/// <summary>
/// A <c>StaticResource</c> used inside a ControlTemplate or DataTemplate is resolved lazily,
/// when the element is first rendered — not at build time. So a renamed brush compiles happily
/// and then takes down the screen the moment someone opens it. These tests check every
/// reference statically, which is what would have caught the Patch screen crash.
/// </summary>
public sealed class XamlResourceTests
{
    private static readonly Regex StaticResourceUse = new(@"\{StaticResource\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex KeyDefinition = new(@"x:Key=""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    private static string AppProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SqlHelper.App")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SqlHelper.App");
    }

    private static IReadOnlyList<string> XamlFiles() =>
        Directory.GetFiles(AppProjectDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    private static HashSet<string> DefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in XamlFiles())
        {
            foreach (Match match in KeyDefinition.Matches(File.ReadAllText(file)))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys;
    }

    [Fact]
    public void Every_StaticResource_reference_resolves_to_a_defined_key()
    {
        HashSet<string> defined = DefinedKeys();

        // Keys WPF itself provides, which never appear as x:Key in our own XAML.
        string[] framework = ["{x:Type Button}", "{x:Type TextBox}"];

        var unresolved = new List<string>();
        foreach (string file in XamlFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match match in StaticResourceUse.Matches(text))
            {
                string name = match.Groups[1].Value;
                if (!defined.Contains(name) && !framework.Contains(name))
                {
                    unresolved.Add($"{Path.GetFileName(file)}: {{StaticResource {name}}}");
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            "These resources are referenced but never defined, so the screen using them will throw "
            + "XamlParseException the moment it renders:\n  " + string.Join("\n  ", unresolved));
    }

    [Fact]
    public void Every_view_and_control_has_a_matching_code_behind_file()
    {
        foreach (string file in XamlFiles().Where(f => !f.EndsWith("App.xaml", StringComparison.Ordinal)))
        {
            Assert.True(File.Exists(file + ".cs"), $"{Path.GetFileName(file)} has no code-behind.");
        }
    }
}
