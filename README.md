# OTIS eView Management Platform API

A .NET 8 client library and sample application for automating the **OTIS eView Multi Pantalla Digital (MPD)** management platform at [clientes-es.sistema-mpd.com](https://clientes-es.sistema-mpd.com).

## Features

- Authenticate and maintain a session with the MPD platform
- Upload images to the library file system
- Create, list, and delete content programmes (slide shows)
- Add images to programmes and configure per-slide display duration
- Create, list, and delete transmission schedules across elevator display locations
- Resolve device locations by name and schedule broadcasts with configurable hours, days, and priority

## Project structure

```
OtisEViewMpd.sln
├── src/
│   └── PC.Elevators.Otis.EView/          # Class library (NuGet-ready)
│       ├── OtisEViewClient.cs
│       └── Models/
│           ├── ContentImage.cs
│           ├── ElevatorTransmission.cs
│           ├── ImageDurationPair.cs
│           └── Programme.cs
└── samples/
    └── EViewSample/                      # Console application
        ├── Program.cs
        └── appsettings.json
```

## Quick start

### 1. Configure credentials

Copy `samples/EViewSample/appsettings.json` to `appsettings.local.json` in the same directory (it is git-ignored) and fill in your credentials:

```json
{
  "Mpd": {
    "BaseUrl": "https://clientes-es.sistema-mpd.com",
    "Username": "your-username",
    "Password": "your-password"
  }
}
```

### 2. Run the sample

```bash
cd samples/EViewSample
dotnet run
```

### 3. Use the library directly

```csharp
using PC.Elevators.Otis.EView;

var client = new OtisEViewClient("https://clientes-es.sistema-mpd.com", "user", "pass");

await client.AuthenticateAsync();

// Upload an image — CSRF token is fetched and cached automatically
await client.UploadPhotoAsync("slide1.jpg", "/Uploads/MyFolder");

// Create a programme
var contentId = await client.CreateContentAsync("Summer Campaign");

// Add the image and set display duration
await client.AddImageToContentAsync(contentId, "slide1.jpg", "/Uploads/MyFolder");
var images = await client.GetContentImagesAsync(contentId);
foreach (var img in images)
    await client.UpdateImageParametersAsync(contentId, img.ImageName, img.ObjectName, img.Index, duration: 10);

// Schedule on elevator displays
await client.CreateTransmissionScheduleAsync(
    contentId,
    locationNames: new List<string> { "Block 1", "Block 2" },
    startDate: "01/06/2025",
    endDate: "31/12/2025",
    startHour: 8,
    endHour: 20
);
```

## Logging

`OtisEViewClient` accepts an optional `ILogger<OtisEViewClient>` as the last constructor argument. Pass one from your DI container or logging factory to receive structured diagnostic output:

```csharp
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var logger = loggerFactory.CreateLogger<OtisEViewClient>();
var client = new OtisEViewClient(baseUrl, username, password, logger);
```

## Requirements

- .NET 8 or later
- Valid OTIS eView MPD account at [clientes-es.sistema-mpd.com](https://clientes-es.sistema-mpd.com)
