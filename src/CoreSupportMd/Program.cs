using System.Globalization;
using System.Text;
using DotnetRelease;
using EndOfLifeDate;
using MarkdownHelpers;

int[] formerVersions = [6, 7, 8];
int[] currentVersions = [6, 8];

var template = "core-support-template.md";
var file = "core-support.md";
var placeholder = "PLACEHOLDER-";
HttpClient client = new();
var stream = File.Open(file, FileMode.Create);
StreamWriter writer = new(stream);
Link pageLinks = new();
Dictionary<int, SupportedOSMatrix?> reports = new();

foreach (var majorVersion in formerVersions)
{
    var version = $"{majorVersion}.0";
    var supportMatrixUrl = ReleaseNotes.GetUri(ReleaseNotes.SupportedOS, version);
    var matrix = await ReleaseNotes.GetSupportedOSes(client, supportMatrixUrl);

    reports.Add(majorVersion, matrix);
}

var targetOsDistros = reports
    .SelectMany(kvp =>
    {
        var m = kvp.Value;
        return m?.Families.SelectMany(
            f => f.Distributions.Select(
                d => new
                {
                    Version = kvp.Key,
                    Distribution = new TargetOsDistribution(f.Name, m.ChannelVersion, d)
                })) ?? [];
    })
    .GroupBy(v => v.Version, v => v.Distribution)
    .ToArray();

var unsupportedTargets = targetOsDistros
    .SelectMany(v => v)
    .ToList();
var unsupportedVersions = GetUnsupportedVersions(unsupportedTargets).ToArray();
var currentTargets = targetOsDistros
    .Where(kvp => currentVersions.Contains(kvp.Key))
    .ToDictionary(v => v.Key, v => (IEnumerable<TargetOsDistribution>)v);

foreach (var line in File.ReadLines(template))
{
    if (!line.StartsWith(placeholder))
    {
        writer.WriteLine(line);
        continue;
    }

    if (line.StartsWith("PLACEHOLDER-FIRST-LEVEL-TARGETS"))
    {
        IEnumerable<TargetOsSelection> targets =
        [
            new("Windows", "windows", "10"),
            new("Windows", "windows", "11"),
            new("Windows", "windows-server"),
            new("Android")
        ];
        await WriteSupportedVersionsSubSectionAsync(writer, client, currentTargets, targets);
    }
    else if (line.StartsWith("PLACEHOLDER-SECOND-LEVEL-TARGETS"))
    {
        IEnumerable<TargetOsSelection> targets =
        [
            new("Windows", "windows", "7"),
            new("Windows", "windows", "8.1"),
            new("Windows", "windows-nano-server"),
            new("Windows", "windows-server-core"),
            new("Linux", "alpine"),
            new("Linux", "ubuntu")
        ];
        await WriteSupportedVersionsSubSectionAsync(writer, client, currentTargets, targets);
    }
    else if (line.StartsWith("PLACEHOLDER-THIRD-LEVEL-TARGETS"))
    {
        IEnumerable<TargetOsSelection> targets =
        [
            new("Linux", "centos"),
            new("Linux", "centos-stream"),
            new("Linux", "debian"),
            new("Linux", "fedora"),
            new("Linux", "opensuse"),
            new("Linux", "rhel"),
            new("Linux", "sles"),
            new("Apple", "macos")
        ];
        await WriteSupportedVersionsSubSectionAsync(writer, client, currentTargets, targets);
    }
    else if (line.StartsWith("PLACEHOLDER-NON-TARGETS"))
    {
        IEnumerable<TargetOsSelection> targets =
        [
            new("Apple", "ios"),
            new("Apple", "ipados")
        ];
        await WriteSupportedVersionsSubSectionAsync(writer, client, currentTargets, targets);
    }
    else if (line.StartsWith("PLACEHOLDER-UNSUPPORTED"))
    {
        await WriteUnSupportedSectionAsync(writer, unsupportedVersions, client);
    }
}

if (pageLinks.Count > 0)
{
    writer.WriteLine();

    foreach (var link in pageLinks.GetReferenceLinkAnchors())
    {
        writer.WriteLine(link);
    }
}

writer.Close();
var writtenFile = File.OpenRead(file);
var length = writtenFile.Length;
var path = writtenFile.Name;
writtenFile.Close();

Console.WriteLine($"Generated {length} bytes");
Console.WriteLine(path);

static async Task WriteSupportedVersionsSubSectionAsync(StreamWriter writer, HttpClient client,
    Dictionary<int, IEnumerable<TargetOsDistribution>> targetDistros, IEnumerable<TargetOsSelection> targetSelectors)
{
    string[] labels = ["**<u>Operating System</u>**", ..GetDotNetColumnNames(targetDistros.Keys), "**Upcoming EoL**"];
    int[] lengths = [32, ..GetDotNetColumnWidths(targetDistros.Count), 24];
    Table table = new(Writer.GetWriter(writer), lengths);

    table.WriteHeader(labels);

    foreach (var target in targetSelectors)
    {
        var targetedReports = GetTargetReposForSelection(targetDistros, target);
        var targetOsDistro = targetedReports.Values
            .LastOrDefault();
        var targetDistro = targetOsDistro?.Distribution;

        var osName = GetTargetOsName(targetDistro, target);
        table.WriteColumn($"**{osName}**");
        
        var allSupportedVersions = GetSupportVersions(targetedReports, target);
        var allEols = targetDistro == null ? [] : await GetVersionsWithEol(targetDistro.Id, allSupportedVersions, client);
        var versionPrefix = target.VersionPrefix;
        foreach (var majorVersion in targetDistros.Keys)
        {
            var result = GetSupportedMajorForTarget(targetDistro, majorVersion, allSupportedVersions, versionPrefix);
            table.WriteColumn(result);
        }

        var supportedVersionsWithEol = GetVersionWithEol(targetDistro, allSupportedVersions, allEols, versionPrefix);
        var lifecycleLink = GetLifecycleAndNextEolVersion(targetDistro, supportedVersionsWithEol);
        table.WriteColumn(lifecycleLink);
        table.EndRow();
    }
}

static async Task WriteUnSupportedSectionAsync(StreamWriter writer, IEnumerable<(string DistributionId, string DistributionName, string Version)> unsupportedVersions, HttpClient client)
{
    // Get all unsupported cycles in parallel.
    var eolCycles = await Task.WhenAll(unsupportedVersions
        .Select(async e => new
        {
            e.DistributionName,
            e.Version,
            Cycle = await GetProductCycle(client, e.DistributionId, e.Version)
        }));

    // Order the list of cycles by their EoL date.
    var orderedEolCycles = eolCycles
        .OrderBy(entry => entry.DistributionName)
        .ThenByDescending(entry => GetEolDateForCycle(entry.Cycle))
        .ToArray();

    if (eolCycles.Length == 0)
    {
        writer.WriteLine("None currently.");
        return;
    }
    
    string[] labels = [ "**OS**", "**Version**", "**End of Life**" ];
    int[] lengths = [24, 16, 24];
    Table table = new(Writer.GetWriter(writer), lengths);
    
    table.WriteHeader(labels);

    foreach (var entry in orderedEolCycles)
    {
        var eol = GetEolTextForCycle(entry.Cycle);
        var distroName = entry.DistributionName;
        var distroVersion = entry.Version;
        if (distroName is "Windows")
        {
            distroVersion = SupportedOS.PrettyifyWindowsVersion(distroVersion);
        }

        table.WriteColumn($"**{distroName}**");
        table.WriteColumn(distroVersion);
        table.WriteColumn(eol);
        table.EndRow();
    }
}

static IEnumerable<(string DistributionId, string DistributionName, string Version)> GetUnsupportedVersions(List<TargetOsDistribution> targetDistros)
{
    var supportedBySome = targetDistros
        .SelectMany(d => d.Distribution.SupportedVersions.Select(v => (d.Distribution.Id, Version: v)))
        .Distinct()
        .GroupBy(v => v.Id, v => v.Version)
        .ToDictionary(v => v.Key, v => (IEnumerable<string>)v);
    var unsupportedBySome =  targetDistros
        .SelectMany(d => d.Distribution.UnsupportedVersions?.Select(v => (d.Distribution.Id, d.Distribution.Name, Version: v)) ?? [])
        .Distinct();

    return unsupportedBySome.Where(v =>
        !supportedBySome.TryGetValue(v.Id, out var val)
        || !val.Contains(v.Version));
}

static string GetLifecycleAndNextEolVersion(SupportDistribution? distro, IEnumerable<VersionWithEol> versionEols)
{
    var upcomingEols = versionEols
        .Where(entry => entry.EolDate != DateOnly.MinValue && entry.EolDate != DateOnly.MaxValue)
        .OrderBy(entry => entry.EolDate);

    var sb = new StringBuilder();
    sb.Append(distro?.Lifecycle != null
        ? Link.Make("Lifecycle", distro.Lifecycle)
        : "No lifecycle");
    foreach (var eol in upcomingEols)
    {
        sb.Append("<br/>");
        sb.Append(eol.Link != null ? Link.Make(eol.Version, eol.Link) : eol.Version);
        sb.Append(" (");
        sb.Append(eol.EolDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture));
        sb.Append(')');

        // Only show all expired and the first new EoL
        if (eol.EolDate > DateOnly.FromDateTime(DateTime.UtcNow)) break;
    }

    return sb.ToString();
}

static string GetEolTextForCycle(SupportCycle? cycle)
{
    var eolDate = GetEolDateForCycle(cycle);
    if (cycle == null || eolDate == DateOnly.MinValue) return "-";

    var result = eolDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var link = cycle.Link;
    if (link != null)
    {
        result = $"[{result}]({link})";
    }
    return result;
}

static async Task<Dictionary<string, SupportCycle?>> GetVersionsWithEol(string id, Dictionary<int, IEnumerable<string>> allVersions, HttpClient client)
{
    // Get all still supported cycles in parallel.
    var eolCycles = await Task.WhenAll(allVersions
        .SelectMany(kvp => kvp.Value)
        .Select(async version => new
        {
            Version = version,
            Cycle = await GetProductCycle(client, id, version)
        }));

    return eolCycles
        .GroupBy(e => e.Version, e => e.Cycle)
        .ToDictionary(v => v.Key, v => v.FirstOrDefault());
}

static async Task<SupportCycle?> GetProductCycle(HttpClient client, string distro, string version)
{
    try
    {
        return await EndOfLifeDate.Product.GetProductCycle(client, distro, version);
    }
    catch (HttpRequestException)
    {
        Console.WriteLine($"No data found at endoflife.date for: {distro} {version}");
        Console.WriteLine();
        return null;
    }
}

static DateOnly GetEolDateForCycle(SupportCycle? supportCycle)
{
    return supportCycle?.GetSupportInfo().EolDate ?? DateOnly.MinValue;
}

static IEnumerable<string> GetDotNetColumnNames(IEnumerable<int> versions) => versions.Select(v => $"**[.NET {v}.0](https://github.com/dotnet/core/blob/main/release-notes/{v}.0/supported-os.md)**");

static IEnumerable<int> GetDotNetColumnWidths(int count) => Enumerable.Repeat(32, count);
static string Join(IEnumerable<string>? strings) => strings is null ? "" : string.Join(", ", strings);

static string GetSupportedMajorForTarget(SupportDistribution? distro, int majorVersion,
    Dictionary<int, IEnumerable<string>> allSupportedVersions, string? versionPrefix)
{
    if (distro == null || !allSupportedVersions.TryGetValue(majorVersion, out var distroVersionsEnum))
    {
        return "(x)";
    }

    var distroName = distro.Name;
    var distroArchitectures = distro.Architectures;
    IList<string> distroVersions = distroVersionsEnum.ToList();
    if (distroVersions.Count is 0)
    {
        return "(x)";
    }
    if (distroName is "Windows")
    {
        distroVersions = SupportedOS.SimplifyWindowsVersions(distroVersions);
    }

    distroVersions = distroVersions.Select(version => StripVersionPrefix(version, versionPrefix)).ToList();

    return $"(/)<br/>" +
           $"Versions: {Join(distroVersions)}<br/>" +
           $"Architectures: {Join(distroArchitectures)}";
}

static Dictionary<int, TargetOsDistribution?> GetTargetReposForSelection(Dictionary<int, IEnumerable<TargetOsDistribution>> targetDistros, TargetOsSelection target)
{
    return targetDistros
        .SelectMany(kvp => kvp.Value.Select(t => new KeyValuePair<int, TargetOsDistribution>(kvp.Key, t)))
        .Where(kvp => target.DoesMatchId(kvp.Value))
        .GroupBy(kvp => kvp.Key, kvp => kvp.Value)
        .ToDictionary(v => v.Key, v => v.FirstOrDefault());
}

static string GetTargetOsName(SupportDistribution? targetDistro, TargetOsSelection target)
{
    var osName = (targetDistro?.Name ?? target.ToString())
                 + (target.VersionPrefix != null
                     ? $" {target.VersionPrefix}"
                     : null);
    if (targetDistro != null)
    {
        osName = Link.Make(osName, targetDistro.Link);
    }

    return osName;
}

static Dictionary<int, IEnumerable<string>> GetSupportVersions(Dictionary<int, TargetOsDistribution?> targetedReports, TargetOsSelection target)
{
    return targetedReports
        .Where(kvp => kvp.Value != null && target.DoesMatchVersion(kvp.Value))
        .SelectMany(kvp => kvp.Value!.Distribution.SupportedVersions.Select(v => (kvp.Key, Version: v)))
        .Where(e => target.VersionPrefix == null || e.Version.StartsWith(target.VersionPrefix))
        .GroupBy(kvp => kvp.Key, kvp => kvp.Version)
        .ToDictionary(v => v.Key, v => (IEnumerable<string>)v);
}

static IEnumerable<VersionWithEol> GetVersionWithEol(SupportDistribution? distro,
    Dictionary<int, IEnumerable<string>> allSupportedVersions, Dictionary<string, SupportCycle?> versionEols,
    string? versionPrefix)
{
    string PrettifyVersion(string version)
    {
        if (distro?.Name is "Windows")
        {
            version = SupportedOS.PrettyifyWindowsVersion(version);
        }

        return StripVersionPrefix(version, versionPrefix);
    }

    return allSupportedVersions
        .SelectMany(kvp => kvp.Value)
        .Distinct()
        .Join(versionEols,
            inner => inner,
            outer => outer.Key,
            (_, eolKvp) => eolKvp)
        .Select(eol => new VersionWithEol(
            PrettifyVersion(eol.Key),
            eol.Value?.Link,
            GetEolDateForCycle(eol.Value)));
}

static string StripVersionPrefix(string version, string? versionPrefix)
{
    if (versionPrefix == null) return version;

    var offset = versionPrefix.Length + 1;
    if (version.Length > offset)
    {
        version = version.Substring(offset);
    }

    return version;
}

public record VersionWithEol(string Version, string? Link, DateOnly EolDate);
public record TargetOsDistribution(string FamilyName, string DotnetVersion, SupportDistribution Distribution);
public record TargetOsSelection(string FamilyName, string? DistributionId = null, string? VersionPrefix = null)
{
    public override string ToString()
    {
        var sb = new StringBuilder(FamilyName);
        if (DistributionId != null)
        {
            sb.Append(' ');
            sb.Append(DistributionId);
        }
        if (VersionPrefix != null)
        {
            sb.Append(" (");
            sb.Append(VersionPrefix);
            sb.Append(')');
        }
        return sb.ToString();
    }

    public bool DoesMatchId(TargetOsDistribution targetOsDistribution)
    {
        if (!targetOsDistribution.FamilyName.Equals(FamilyName)) return false;
        if (DistributionId != null && !targetOsDistribution.Distribution.Id.Equals(DistributionId)) return false;

        return true;
    }

    public bool DoesMatchVersion(TargetOsDistribution targetOsDistribution)
    {
        if (VersionPrefix != null && !targetOsDistribution.Distribution.SupportedVersions.Any(v => v.StartsWith(VersionPrefix))) return false;

        return true;
    }
}