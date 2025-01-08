using DotnetRelease;

if (args.Length is 0 || !int.TryParse(args[0], out int majorVersion))
{
    ReportInvalidArgs();
    return;
}

string? baseUrl = args.Length > 1 ? args[1] : null;

string version = $"{majorVersion}.0";
string supportMatrixUrl = ReleaseNotes.GetUri(ReleaseNotes.SupportedOS, version, baseUrl);
string releaseUrl = ReleaseNotes.GetUri(ReleaseNotes.Releases, version, baseUrl);
bool preferWeb = supportMatrixUrl.StartsWith("https");
SupportedOSMatrix? matrix = null;
MajorReleaseOverview? majorRelease = null;

if (preferWeb)
{
    HttpClient client = new();
    matrix = await ReleaseNotes.GetSupportedOSes(client, supportMatrixUrl);
    majorRelease = await ReleaseNotes.GetMajorRelease(client, releaseUrl);
}   
else
{
    matrix = await ReleaseNotes.GetSupportedOSes(File.OpenRead(supportMatrixUrl));
    majorRelease = await ReleaseNotes.GetMajorRelease(File.OpenRead(releaseUrl));
}

var report = await ReleaseReportGenerator.GetReportOverviewAsync(matrix, majorRelease);

var reportVersion = report.Version;
Console.WriteLine($"* .NET {reportVersion}");
if (majorRelease?.SupportPhase == SupportPhase.Eol)
{
    Console.WriteLine("** This version is EOL and therefore checks can not be performed as the documents should show the state as of EOL instead of today.");
    return;
}
foreach (var family in report.Families)
{
    foreach (var distribution in family.Distributions)
    {
        var distroName = distribution.Name;
        var distroExceptions = distribution.Exceptions.ToDictionary(e => e.Version, e => e.Note);

        foreach (var dVersion in distribution.ActiveReleasesEOLSoon)
        {
            PrintIfNotExpected(distroName, dVersion, distroExceptions, "EOL Soon");
        }
        foreach (var dVersion in distribution.NotActiveReleasesSupported)
        {
            PrintIfNotExpected(distroName, dVersion, distroExceptions, "EOL but still supported");
        }
        foreach (var dVersion in distribution.ReleasesMissing)
        {
            PrintIfNotExpected(distroName, dVersion, distroExceptions, "Currently missing");
        }
    }
}


static void ReportInvalidArgs()
{
    Console.WriteLine("Invalid args.");
    Console.WriteLine("Expected: version [URL or Path, absolute or root location]");
}

void PrintIfNotExpected(string distroName, string distroVersion, IReadOnlyDictionary<string, string> distroExceptions, string text)
{
    if (distroExceptions?.ContainsKey(distroVersion) == true) return;

    Console.WriteLine($"** {distroName} {distroVersion}: {text}");
}