using System.IO;
using System.Text.RegularExpressions;

namespace SqlHelper.App.Tests;

/// <summary>
/// The tool's central promise is that nothing leaves the PC it runs on: connection details and
/// passwords stay local, and the only thing it ever talks to is the SQL Servers the operator
/// registered themselves. That promise is easy to break by accident — one convenience call to
/// check for updates, report an error, or fetch something would do it — so it is asserted here
/// against the source rather than left to review.
/// </summary>
public sealed class NoNetworkEgressTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SqlHelper.App")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<string> SourceFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    // Anything here would open a connection to somewhere the operator did not choose.
    private static readonly string[] ForbiddenApis =
    [
        "HttpClient", "WebClient", "HttpWebRequest", "WebRequest", "ClientWebSocket",
        "TcpClient", "UdpClient", "SmtpClient", "new Socket(", "HttpMessageHandler",
    ];

    [Fact]
    public void No_source_file_can_reach_the_network()
    {
        var offenders = new List<string>();

        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (string api in ForbiddenApis)
            {
                if (text.Contains(api, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} uses {api}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_source_file_hard_codes_an_outbound_url()
    {
        // Namespaces and XML doc links are xmlns/schemas URLs, not somewhere data could be sent.
        var offenders = new List<string>();
        Regex url = new(@"https?://(?!schemas\.|www\.w3\.org|learn\.microsoft\.com|docs\.microsoft\.com)[\w.-]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (string file in SourceFiles())
        {
            foreach (Match match in url.Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_only_third_party_packages_are_local_processing_libraries()
    {
        // A new package is the most likely way telemetry would arrive, so the list is pinned.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ClosedXML",                                    // writing .xlsx files
            "DiffPlex",                                     // computing diffs
            "Microsoft.Data.SqlClient",                     // talking to the registered servers
            "Microsoft.SqlServer.TransactSql.ScriptDom",    // parsing T-SQL
            "System.Security.Cryptography.ProtectedData",   // DPAPI
            "CommunityToolkit.Mvvm",                        // UI plumbing
        };

        var found = new List<string>();
        Regex reference = new(@"PackageReference\s+Include=""([^""]+)""", RegexOptions.CultureInvariant);

        foreach (string project in Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match match in reference.Matches(File.ReadAllText(project)))
            {
                found.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(found);
        Assert.Empty(found.Where(p => !allowed.Contains(p)));
    }
}
