using Microsoft.Extensions.Configuration;
using PC.Elevators.Otis.EView;
using PC.Elevators.Otis.EView.Models;

// ---------------------------------------------------------------------------
// Build configuration from appsettings.json
// ---------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)  // machine-local overrides, git-ignored
    .Build();

var baseUrl  = config["Mpd:BaseUrl"]  ?? throw new InvalidOperationException("Mpd:BaseUrl is required");
var username = config["Mpd:Username"] ?? throw new InvalidOperationException("Mpd:Username is required");
var password = config["Mpd:Password"] ?? throw new InvalidOperationException("Mpd:Password is required");

var targetDirectory = config["Sample:TargetDirectory"] ?? "/Uploads/SampleApp";
var programmeName   = config["Sample:ProgrammeName"]   ?? "Sample Programme";
var locations       = config.GetSection("Sample:Locations").Get<List<string>>() ?? new List<string>();
var startDate       = config["Sample:StartDate"] ?? "01/01/2025";
var endDate         = config["Sample:EndDate"]   ?? "31/12/2025";
var startHour       = int.TryParse(config["Sample:StartHour"], out var sh) ? sh : 0;
var endHour         = int.TryParse(config["Sample:EndHour"],   out var eh) ? eh : 23;

var imageDurations = config.GetSection("Sample:ImageDurations")
    .Get<List<ImageDurationConfig>>() ?? new List<ImageDurationConfig>();

// ---------------------------------------------------------------------------
// Run the full upload workflow
// ---------------------------------------------------------------------------
var client = new OtisEViewClient(baseUrl, username, password);

Console.WriteLine("=== OtisEView MPD Sample ===");
Console.WriteLine($"Platform : {baseUrl}");
Console.WriteLine($"User     : {username}");
Console.WriteLine();

// 1. Authenticate
Console.Write("Authenticating... ");
if (!await client.AuthenticateAsync())
{
    Console.WriteLine("FAILED. Check credentials.");
    return 1;
}
Console.WriteLine($"OK  (session: {client.SessionId})");


// 3. Create upload folder
Console.Write($"Creating folder '{targetDirectory}'... ");
var folderName   = Path.GetFileName(targetDirectory.TrimEnd('/'));
var parentFolder = Path.GetDirectoryName(targetDirectory.Replace('/', Path.DirectorySeparatorChar))
                       ?.Replace(Path.DirectorySeparatorChar, '/') ?? "/Uploads";
if (!await client.CreateFolderAsync(folderName, parentFolder))
    Console.WriteLine("(may already exist — continuing)");
else
    Console.WriteLine("OK");

// 4. Upload images
var uploaded = new List<string>();
foreach (var item in imageDurations)
{
    Console.Write($"Uploading {item.FilePath}... ");
    if (await client.UploadPhotoAsync(item.FilePath, targetDirectory))
    {
        uploaded.Add(Path.GetFileName(item.FilePath));
        Console.WriteLine("OK");
    }
    else
    {
        Console.WriteLine("FAILED (skipping)");
    }
}

if (uploaded.Count == 0)
{
    Console.WriteLine("No images uploaded. Exiting.");
    return 1;
}

// 5. Create content programme
Console.Write($"Creating programme '{programmeName}'... ");
var contentId = await client.CreateContentAsync(programmeName);
if (contentId is null)
{
    Console.WriteLine("FAILED.");
    return 1;
}
Console.WriteLine($"OK  (ID: {contentId})");

// 6. Add images to programme and set durations
for (int i = 0; i < uploaded.Count; i++)
{
    var fileName = uploaded[i];
    var duration = i < imageDurations.Count ? imageDurations[i].DurationSeconds : 10;

    Console.Write($"  Adding slide {i + 1}/{uploaded.Count}: {fileName} ({duration}s)... ");
    if (!await client.AddImageToContentAsync(contentId, fileName, targetDirectory))
    {
        Console.WriteLine("FAILED (skipping)");
        continue;
    }

    // Retrieve the object name assigned by the editor
    var images = await client.GetContentImagesAsync(contentId);
    var slide  = images.FirstOrDefault(img => img.ImageName == fileName || img.ObjectName.Contains(fileName));
    if (slide is not null)
    {
        await client.UpdateImageParametersAsync(
            contentId, slide.ImageName, slide.ObjectName, slide.Index, duration);
        Console.WriteLine("OK");
    }
    else
    {
        Console.WriteLine("added (could not update duration)");
    }
}

// 7. Create transmission schedule
if (locations.Count > 0)
{
    Console.WriteLine();
    Console.Write($"Scheduling on [{string.Join(", ", locations)}]... ");
    var scheduled = await client.CreateTransmissionScheduleAsync(
        contentId, locations, startDate, endDate, startHour, endHour);
    Console.WriteLine(scheduled ? "OK" : "FAILED");
}
else
{
    Console.WriteLine("No locations configured — skipping scheduling step.");
}

// 8. Print current transmissions
Console.WriteLine();
Console.WriteLine("Current transmissions:");
var transmissions = await client.ListCurrentTransmissionsAsync();
if (transmissions.Count == 0)
{
    Console.WriteLine("  (none)");
}
else
{
    Console.WriteLine($"  {"PrgId",-8} {"Location",-20} {"Content",-25} {"Status",-30} Period");
    Console.WriteLine($"  {new string('-', 100)}");
    foreach (var t in transmissions)
    {
        Console.WriteLine($"  {t.ProgrammingId,-8} {t.Location,-20} {t.ContentName,-25} {t.TransmissionStatus,-30} {t.StartDate}–{t.EndDate}");
    }
}

Console.WriteLine();
Console.WriteLine("Done.");
return 0;

// ---------------------------------------------------------------------------
// Local helper record (mirrors appsettings.json Sample:ImageDurations items)
// ---------------------------------------------------------------------------
record ImageDurationConfig
{
    public string FilePath { get; init; } = "";
    public int DurationSeconds { get; init; } = 10;
}
