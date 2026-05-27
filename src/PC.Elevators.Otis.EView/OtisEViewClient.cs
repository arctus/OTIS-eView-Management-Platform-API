using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PC.Elevators.Otis.EView.Models;

namespace PC.Elevators.Otis.EView;

/// <summary>
/// Client for automating interactions with the OTIS eView Multi Pantalla Digital (MPD) platform
/// at <see href="https://clientes-es.sistema-mpd.com"/>.
/// </summary>
public class OtisEViewClient
{
    #region Private fields

    private readonly string _baseUrl;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _client;
    private readonly ILogger<OtisEViewClient>? _logger;
    private string? _csrfToken;

    #endregion

    #region Public properties

    /// <summary>PHP session ID extracted after a successful call to <see cref="AuthenticateAsync"/>.</summary>
    public string? SessionId { get; private set; }

    #endregion

    #region Public constructors

    /// <param name="baseUrl">Base URL of the MPD platform, e.g. <c>https://clientes-es.sistema-mpd.com</c>.</param>
    /// <param name="username">MPD account username.</param>
    /// <param name="password">MPD account password.</param>
    /// <param name="logger">Optional logger; pass <see langword="null"/> to suppress all log output.</param>
    public OtisEViewClient(string baseUrl, string username, string password, ILogger<OtisEViewClient>? logger = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;

        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _client = new HttpClient(_handler) { Timeout = TimeSpan.FromMinutes(5) };
        ConfigureDefaultHeaders();
    }

    #endregion

    #region Public methods

    /// <summary>Authenticates with the MPD platform and stores the session cookie.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    public async Task<bool> AuthenticateAsync()
    {
        try
        {
            var response = await _client.GetAsync($"{_baseUrl}/index.php");
            if (!response.IsSuccessStatusCode)
                return false;

            ExtractSessionId();
            SetScreenDimensionsCookie();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Authentication error");
            return false;
        }
    }


    /// <summary>Lists image file names in <paramref name="directory"/> using the library browser.</summary>
    public async Task<List<string>> ListImagesAsync(string directory)
    {
        var images = new List<string>();
        try
        {
            var csrf = await GetCSRFTokenAsync();
            if (csrf is null) return images;
            var encodedDir = EncodeDirectoryPath(directory);
            var url = $"{_baseUrl}/editor2/ficheros.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrf}&dir={encodedDir}&operacion=0";
            var request = CreateGetRequest(url);
            request.Headers.Add("Referer",
                $"{_baseUrl}/editor2/carpetas.php?modolibreria=1&id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrf}&dir={encodedDir}&operacion=0");
            request.Headers.Add("Sec-Fetch-Dest", "iframe");
            var response = await _client.SendAsync(request);
            if (response.IsSuccessStatusCode)
                images = ExtractImageNamesFromHtml(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error listing images in {Directory}", directory);
        }
        return images;
    }

    /// <summary>Uploads a local image file to the MPD library.</summary>
    /// <param name="filePath">Absolute path to the local image file.</param>
    /// <param name="targetDirectory">Target library directory path (default <c>/Uploads</c>).</param>
    /// <returns><see langword="true"/> on success.</returns>
    public async Task<bool> UploadPhotoAsync(string filePath, string targetDirectory = "/Uploads")
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger?.LogWarning("File not found: {FilePath}", filePath);
                return false;
            }

            var csrf = await GetCSRFTokenAsync();
            if (csrf is null)
            {
                _logger?.LogError("Cannot upload — CSRF token unavailable");
                return false;
            }

            var fileName = Path.GetFileName(filePath);
            var fileBytes = File.ReadAllBytes(filePath);
            var fileSize = fileBytes.Length;
            var (imgWidth, imgHeight) = ReadImageDimensions(filePath);

            _logger?.LogInformation(
                "Uploading {FileName} ({FileSize} bytes, {Width}x{Height}) to {Directory}",
                fileName, fileSize, imgWidth, imgHeight, targetDirectory);

            var encodedDir = EncodeDirectoryPath(targetDirectory);
            await NavigateToTargetFolder(targetDirectory, encodedDir, csrf);

            if (!await ValidateUpload(fileName, fileSize, imgWidth, imgHeight, encodedDir, csrf))
                return false;

            if (!await PerformUpload(fileName, fileBytes, fileSize, imgWidth, imgHeight, encodedDir, csrf))
                return false;

            await RefreshFileListing(encodedDir, csrf);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Upload error for {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>Deletes an image file from the MPD library.</summary>
    public async Task<bool> DeleteImageAsync(string fileName, string directory)
    {
        try
        {
            var csrf = await GetCSRFTokenAsync();
            if (csrf is null)
            {
                _logger?.LogError("Cannot delete image — CSRF token unavailable");
                return false;
            }

            _logger?.LogDebug("Deleting {FileName} from {Directory}", fileName, directory);
            var url = $"{_baseUrl}/editor2/operaciones_carpeta.php"
                + $"?id_directorio=&modolibreria=0&codigo_anticsrf={csrf}&solorecarga=yes"
                + $"&dir={Uri.EscapeDataString(directory)}&archivo={Uri.EscapeDataString(fileName)}"
                + "&accion=si&operacion=4&suboperacion=eliminararchivo";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Referer", $"{_baseUrl}/editor2/index.php?idsesion={SessionId}&id_contenido=&libreria=1&momento=");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError("Delete failed ({StatusCode}): {Result}",
                    response.StatusCode, await response.Content.ReadAsStringAsync());
                return false;
            }

            await RefreshFileListing(EncodeDirectoryPath(directory), csrf);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Delete error for {FileName}", fileName);
            return false;
        }
    }

    /// <summary>Creates a new folder in the MPD library.</summary>
    public async Task<bool> CreateFolderAsync(string folderName, string parentDirectory)
    {
        try
        {
            var csrf = await GetCSRFTokenAsync();
            if (csrf is null)
            {
                _logger?.LogError("Cannot create folder — CSRF token unavailable");
                return false;
            }

            _logger?.LogInformation("Creating folder {FolderName} in {ParentDirectory}", folderName, parentDirectory);
            var propaga = $"id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrf}&";
            var formData = new Dictionary<string, string>
            {
                { "nombre",           folderName },
                { "directorio",       parentDirectory },
                { "propaga",          propaga },
                { "operacion",        "1" },
                { "codigo_anticsrf",  csrf }
            };

            var request = CreateFormPostRequest($"{_baseUrl}/editor2/operaciones_carpeta.php?modolibreria=1", formData);
            request.Headers.Add("Referer",
                $"{_baseUrl}/editor2/carpetas.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrf}"
                + $"&dir={Uri.EscapeDataString(parentDirectory)}&operacion=1&modolibreria=1");

            var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError("Create folder failed ({StatusCode}): {Result}",
                    response.StatusCode, await response.Content.ReadAsStringAsync());
                return false;
            }

            _logger?.LogInformation("Folder {FolderName} created", folderName);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Create folder error for {FolderName}", folderName);
            return false;
        }
    }

    /// <summary>Deletes a folder from the MPD library.</summary>
    public async Task<bool> DeleteFolderAsync(string folderName, string parentDirectory)
    {
        try
        {
            var csrf = await GetCSRFTokenAsync();
            if (csrf is null)
            {
                _logger?.LogError("Cannot delete folder — CSRF token unavailable");
                return false;
            }

            _logger?.LogInformation("Deleting folder {FolderName} from {ParentDirectory}", folderName, parentDirectory);
            var encodedDirForUrl = Uri.EscapeDataString(parentDirectory);
            var encodedDir = EncodeDirectoryPath(parentDirectory);

            await NavigateToParentForDelete(encodedDir, encodedDirForUrl, csrf);

            if (!await SendFolderDeleteRequest(folderName, encodedDirForUrl, csrf))
                return false;

            if (!await ConfirmFolderDelete(folderName, encodedDirForUrl, csrf))
                return false;

            _logger?.LogInformation("Folder {FolderName} deleted", folderName);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Delete folder error for {FolderName}", folderName);
            return false;
        }
    }

    /// <summary>Creates a new content programme (slide show) in the MPD platform.</summary>
    /// <param name="contentName">Display name of the programme.</param>
    /// <param name="contentType">Content type; defaults to <c>"normal"</c>.</param>
    /// <returns>The new content ID, or <see langword="null"/> on failure.</returns>
    public async Task<string?> CreateContentAsync(string contentName, string contentType = "normal")
    {
        try
        {
            _logger?.LogInformation("Creating content {ContentName} (type: {ContentType})", contentName, contentType);

            var formRequest = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/nuevo_contenido.php");
            formRequest.Headers.Add("Accept", "*/*");
            formRequest.Headers.Add("Referer", $"{_baseUrl}/listado_mpd_contenido.php?tipo=0");
            formRequest.Headers.Add("Sec-Fetch-Dest", "empty");
            formRequest.Headers.Add("Sec-Fetch-Mode", "cors");
            formRequest.Headers.Add("Sec-Fetch-Site", "same-origin");
            formRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var formResponse = await _client.SendAsync(formRequest);
            if (!formResponse.IsSuccessStatusCode)
            {
                _logger?.LogError("Failed to get content creation form ({StatusCode})", formResponse.StatusCode);
                return null;
            }

            var csrfToken = ExtractCSRFTokenFromHtml(await formResponse.Content.ReadAsStringAsync());
            if (string.IsNullOrEmpty(csrfToken))
            {
                _logger?.LogError("Failed to extract CSRF token from content creation form");
                return null;
            }

            var formData = new Dictionary<string, string>
            {
                { "archivo",                   contentName },
                { "codigo_anticsrf",           csrfToken },
                { "tipo",                      contentType },
                { "id_popup_nuevo_contenido",  "" }
            };

            var createRequest = CreateFormPostRequest($"{_baseUrl}/nuevo_contenido2.php", formData);
            createRequest.Headers.Add("Referer", $"{_baseUrl}/listado_mpd_contenido.php?tipo=0");
            createRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var createResponse = await _client.SendAsync(createRequest);
            var createResult = await createResponse.Content.ReadAsStringAsync();
            if (!createResponse.IsSuccessStatusCode)
            {
                _logger?.LogError("Content creation failed ({StatusCode}): {Result}", createResponse.StatusCode, createResult);
                return null;
            }

            var contentId = ExtractContentIdFromResponse(createResult);
            if (string.IsNullOrEmpty(contentId))
            {
                _logger?.LogError("Failed to extract content ID from creation response");
                return null;
            }

            _logger?.LogInformation("Content {ContentName} created with ID {ContentId}", contentName, contentId);
            await LoadEditorInterface(contentId);
            return contentId;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Create content error for {ContentName}", contentName);
            return null;
        }
    }

    /// <summary>Adds an image from the library to an existing content programme.</summary>
    public async Task<bool> AddImageToContentAsync(string contentId, string imageName, string directory)
    {
        try
        {
            _logger?.LogDebug("Adding {ImageName} from {Directory} to content {ContentId}", imageName, directory, contentId);
            var url = $"{_baseUrl}/editor2/operaciones_componer.php"
                + $"?id_contenido={contentId}&modolibreria=1"
                + $"&directorio={Uri.EscapeDataString(directory)}"
                + $"&nombre={Uri.EscapeDataString(imageName)}&operacion=7";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Referer",
                $"{_baseUrl}/editor2/ficheros.php?id_contenido={contentId}&modolibreria=1&momento=&codigo_anticsrf=&dir={Uri.EscapeDataString(directory)}&operacion=0");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError("Failed to add {ImageName} to content {ContentId} ({StatusCode}): {Result}",
                    imageName, contentId, response.StatusCode, await response.Content.ReadAsStringAsync());
                return false;
            }

            _logger?.LogDebug("Image {ImageName} added to content {ContentId}", imageName, contentId);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Add image error for {ImageName}", imageName);
            return false;
        }
    }

    /// <summary>Updates the display parameters (duration, transition, type) of a slide within a programme.</summary>
    /// <param name="contentId">Content programme ID.</param>
    /// <param name="imageName">File name of the image.</param>
    /// <param name="objectName">Object name as reported by the editor.</param>
    /// <param name="imageIndex">Zero-based slide index within the programme.</param>
    /// <param name="duration">Display duration in seconds (default 10).</param>
    /// <param name="imageType">Layout type, e.g. <c>"image_top"</c>.</param>
    /// <param name="fadeIn">Fade-in duration in seconds.</param>
    /// <param name="fadeOut">Fade-out duration in seconds.</param>
    /// <param name="csrfToken">Optional CSRF token; fetched automatically when omitted.</param>
    public async Task<bool> UpdateImageParametersAsync(
        string contentId,
        string imageName,
        string objectName,
        int imageIndex,
        int duration = 10,
        string imageType = "image_top",
        double fadeIn = 0.0,
        double fadeOut = 0.0,
        string? csrfToken = null)
    {
        try
        {
            _logger?.LogDebug("Updating parameters for {ImageName} (index: {Index}, duration: {Duration}s)",
                imageName, imageIndex, duration);

            csrfToken ??= await GetImageCSRFTokenAsync(contentId, imageIndex);

            var formData = new Dictionary<string, string>
            {
                { "nombre",      imageName },
                { "id_contenido", contentId },
                { "numeracion",  imageIndex.ToString() },
                { "objeto",      objectName },
                { "tipo",        imageType },
                { "duracion",    duration.ToString() },
                { "sl_duracion", $"{duration} seconds" },
                { "fadein",      fadeIn.ToString("F1") },
                { "fadeout",     fadeOut.ToString("F1") },
                { "coordx",      "" },
                { "coordy",      "" }
            };

            if (!string.IsNullOrEmpty(csrfToken))
                formData["codigo_anticsrf"] = csrfToken;

            var request = CreateFormPostRequest($"{_baseUrl}/editor2/parametros.php", formData);
            request.Headers.Add("Referer",
                $"{_baseUrl}/editor2/parametros.php?id_contenido={contentId}&numeracion={imageIndex}&nombre=");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError("Failed to update parameters for {ImageName} ({StatusCode}): {Result}",
                    imageName, response.StatusCode, await response.Content.ReadAsStringAsync());
                return false;
            }

            _logger?.LogDebug("Parameters updated for {ImageName}", imageName);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Update parameters error for {ImageName}", imageName);
            return false;
        }
    }

    /// <summary>Lists image file names available within a directory for a given content programme.</summary>
    public async Task<List<string>> ListImagesInDirectoryAsync(string contentId, string directory)
    {
        var images = new List<string>();
        try
        {
            var encodedDir = Uri.EscapeDataString(directory);
            var url = $"{_baseUrl}/editor2/ficheros.php?id_contenido={contentId}&modolibreria=1&momento=&codigo_anticsrf=&dir={encodedDir}&operacion=0";
            var request = CreateGetRequest(url);
            request.Headers.Add("Referer",
                $"{_baseUrl}/editor2/carpetas.php?id_contenido={contentId}&modolibreria=1&momento=&codigo_anticsrf=&dir={encodedDir}&operacion=0");
            request.Headers.Add("Sec-Fetch-Dest", "iframe");
            var response = await _client.SendAsync(request);
            if (response.IsSuccessStatusCode)
                images = ExtractImageNamesFromHtml(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error listing images in directory {Directory}", directory);
        }
        return images;
    }

    /// <summary>Returns all images currently composing a content programme, in slide order.</summary>
    public async Task<List<ContentImage>> GetContentImagesAsync(string contentId)
    {
        var contentImages = new List<ContentImage>();
        try
        {
            var response = await _client.GetAsync($"{_baseUrl}/editor2/componer.php?id_contenido={contentId}");
            if (!response.IsSuccessStatusCode)
                return contentImages;

            var html = await response.Content.ReadAsStringAsync();
            var matches = Regex.Matches(html,
                @"numeracion=(\d+).*?nombre=([^&'""]+\.(?:jpg|png|gif|jpeg|webp))",
                RegexOptions.IgnoreCase);

            var seen = new HashSet<int>();
            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, out int index) || !seen.Add(index))
                    continue;

                var name = m.Groups[2].Value;
                contentImages.Add(new ContentImage { ImageName = name, ObjectName = name, Index = index });
            }

            contentImages.Sort((a, b) => a.Index.CompareTo(b.Index));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting content images for {ContentId}", contentId);
        }
        return contentImages;
    }

    /// <summary>Returns all content programmes visible in the MPD platform.</summary>
    public async Task<List<Programme>> ListProgrammesAsync()
    {
        var programmes = new List<Programme>();
        try
        {
            var url = $"{_baseUrl}/listado_mpd_contenido.php?tipo=0";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Referer", url);
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return programmes;

            var html = await response.Content.ReadAsStringAsync();
            var rows = Regex.Matches(html, @"<tr class=""r\d+"">(.*?)</tr>", RegexOptions.Singleline);

            foreach (Match row in rows)
            {
                var r = row.Groups[1].Value;
                var idMatch = Regex.Match(r, @"name=""c(\d+)""");
                if (!idMatch.Success) continue;

                var dateMatch = Regex.Match(r, @"&nbsp;(\d{2}/\d{2}/\d{4})&nbsp;");
                var countMatch = Regex.Match(r, @"<td>&nbsp;(\d+)&nbsp;</td>");
                var nameMatch = Regex.Match(r,
                    @"<td>&nbsp;(\d{2}/\d{2}/\d{4})&nbsp;</td>\s*<td>&nbsp;\d+&nbsp;</td>\s*<td>&nbsp;([^&<]+)&nbsp;</td>");

                programmes.Add(new Programme
                {
                    ContentId = idMatch.Groups[1].Value,
                    DateOfInsertion = dateMatch.Success ? dateMatch.Groups[1].Value : "",
                    NumberOfImages = countMatch.Success ? int.Parse(countMatch.Groups[1].Value) : 0,
                    Name = nameMatch.Success ? nameMatch.Groups[2].Value : ""
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error listing programmes");
        }
        return programmes;
    }

    /// <summary>Deletes a content programme by its content ID.</summary>
    public async Task<bool> DeleteProgrammeAsync(string contentId)
    {
        try
        {
            _logger?.LogInformation("Deleting programme {ContentId}", contentId);

            var request1 = CreateFormPostRequest($"{_baseUrl}/borrar_lista_contenidos.php",
                new Dictionary<string, string> { { $"c{contentId}", "on" }, { "tipo", "0" } });
            request1.Headers.Add("Referer", $"{_baseUrl}/listado_mpd_contenido.php?tipo=0");
            request1.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response1 = await _client.SendAsync(request1);
            var result1 = await response1.Content.ReadAsStringAsync();
            if (!response1.IsSuccessStatusCode)
            {
                _logger?.LogError("Deletion confirmation failed ({StatusCode}): {Result}", response1.StatusCode, result1);
                return false;
            }

            var csrfMatch = Regex.Match(result1, @"codigo_anticsrf=([a-f0-9]+\.\d+)");
            if (!csrfMatch.Success)
            {
                _logger?.LogError("Failed to extract CSRF token from deletion confirmation response");
                return false;
            }

            await Task.Delay(1000);

            var deleteUrl = $"{_baseUrl}/borrar_lista_contenidos2.php?tipo=0&tabla=mpd_contenido"
                + $"&codigo_anticsrf={csrfMatch.Groups[1].Value}&contenidos_borrar={contentId}";
            var request2 = new HttpRequestMessage(HttpMethod.Get, deleteUrl);
            request2.Headers.Add("Accept", "*/*");
            request2.Headers.Add("Referer", $"{_baseUrl}/listado_mpd_contenido.php?tipo=0");
            request2.Headers.Add("Sec-Fetch-Dest", "empty");
            request2.Headers.Add("Sec-Fetch-Mode", "cors");
            request2.Headers.Add("Sec-Fetch-Site", "same-origin");
            request2.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response2 = await _client.SendAsync(request2);
            bool ok = response2.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect
                      || response2.IsSuccessStatusCode;

            if (ok)
                _logger?.LogInformation("Programme {ContentId} deleted", contentId);

            return ok;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Delete programme error for {ContentId}", contentId);
            return false;
        }
    }

    /// <summary>Returns all current transmission schedules, optionally filtered by zone (default zone 1).</summary>
    public async Task<List<ElevatorTransmission>> ListCurrentTransmissionsAsync(int zone = 1)
    {
        var all = new List<ElevatorTransmission>();
        try
        {
            int page = 1, totalPages = 1;
            do
            {
                _logger?.LogDebug("Fetching transmissions page {Page}", page);

                var url = page == 1
                    ? $"{_baseUrl}/main.php?momento=actualidad&zona={zone}"
                    : $"{_baseUrl}/main.php?orden=fecha_fin%20DESC&lista_pag={page}&momento=actualidad&tipo=0&codigo_anticsrf=";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                request.Headers.Add("Referer", page == 1
                    ? $"{_baseUrl}/menu.php?zona=0&zonaC={zone}&empresa=OTIS&id_pais=73"
                    : $"{_baseUrl}/main.php?orden=fecha_fin%20DESC&lista_pag={page - 1}&momento=actualidad&tipo=0&codigo_anticsrf=");
                request.Headers.Add("Sec-Fetch-Dest", "iframe");
                request.Headers.Add("Sec-Fetch-Mode", "navigate");
                request.Headers.Add("Sec-Fetch-Site", "same-origin");
                request.Headers.Add("Sec-Fetch-User", "?1");
                request.Headers.Add("Upgrade-Insecure-Requests", "1");

                var response = await _client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    break;

                var html = await response.Content.ReadAsStringAsync();

                if (page == 1)
                {
                    var pageMatch = Regex.Match(html, @"Page\s+\d+/(\d+)");
                    if (pageMatch.Success)
                    {
                        totalPages = int.Parse(pageMatch.Groups[1].Value);
                        _logger?.LogDebug("Total transmission pages: {TotalPages}", totalPages);
                    }
                }

                var rows = Regex.Matches(html, @"<tr class=""r\d+"">(.*?)</tr>", RegexOptions.Singleline);
                foreach (Match row in rows)
                {
                    var r = row.Groups[1].Value;

                    var prgIdMatch = Regex.Match(r, @"name=""p(\d+)""");
                    if (!prgIdMatch.Success) continue;

                    var locationMatch = Regex.Match(r, @"<td>&nbsp;([^&<]+)&nbsp;</td>");
                    var deviceMatch = Regex.Match(r,
                        @"title=""<b>([^<]+)</b><br><br><b>Address: </b>([^<]+)<br><b>Town: </b>([^<]+)<br><b>Province: </b>([^<]+)<br>");
                    var programMatch = Regex.Match(r,
                        @"<b>Current program: </b>([^<]+)<br>"" >([^<]+)</a>");
                    var contentMatch = Regex.Match(r,
                        @"title=""<b>([^<]+)</b><br><br><b>ID\.: </b>(\d+)<br><b>Date of Insertion: </b>([^<]+)<br><b>No\. of Images: </b>(\d+)");
                    var dateTds = Regex.Matches(r, @"<td>&nbsp;(\d{2}/\d{2}/\d{4})&nbsp;</td>");
                    var timeTds = Regex.Matches(r, @"<td>&nbsp;&nbsp;(\d+:\d+)&nbsp;</td>");
                    var daysMatch = Regex.Match(r, @"<td>&nbsp;([MTuWThFSa]+Su?)&nbsp;</td>");
                    var priorityMatch = Regex.Match(r,
                        @"<td>&nbsp;([^&<]+)&nbsp;</td>[\s\S]*?<td>&nbsp;[MTuWThFSa]+");
                    var huecoMatch = Regex.Match(r, @"id_hueco=(\d+)");

                    var status = r switch
                    {
                        var s when s.Contains("cuadrado_verde.png")    => "Transmitted",
                        var s when s.Contains("cuadrado_rojo.png")     => "Not transmitted",
                        var s when s.Contains("cuadrado_amarillo.png") => "Transmission in progress",
                        var s when s.Contains("cuadrado_naranja.png")  => "Transmission problems",
                        _                                               => "Unknown"
                    };

                    all.Add(new ElevatorTransmission
                    {
                        ProgrammingId    = prgIdMatch.Groups[1].Value,
                        Location         = locationMatch.Success ? locationMatch.Groups[1].Value.Trim() : "",
                        DeviceId         = deviceMatch.Success ? deviceMatch.Groups[1].Value : "",
                        Address          = deviceMatch.Success ? deviceMatch.Groups[2].Value : "",
                        Town             = deviceMatch.Success ? deviceMatch.Groups[3].Value : "",
                        Province         = deviceMatch.Success ? deviceMatch.Groups[4].Value : "",
                        CurrentProgram   = programMatch.Success ? programMatch.Groups[1].Value : "",
                        ContentName      = contentMatch.Success ? contentMatch.Groups[1].Value : "",
                        ContentId        = contentMatch.Success ? contentMatch.Groups[2].Value : "",
                        DateOfInsertion  = contentMatch.Success ? contentMatch.Groups[3].Value : "",
                        NumberOfImages   = contentMatch.Success ? int.Parse(contentMatch.Groups[4].Value) : 0,
                        StartDate        = dateTds.Count > 0 ? dateTds[0].Groups[1].Value : "",
                        EndDate          = dateTds.Count > 1 ? dateTds[1].Groups[1].Value : "",
                        StartTime        = timeTds.Count > 0 ? timeTds[0].Groups[1].Value : "",
                        EndTime          = timeTds.Count > 1 ? timeTds[1].Groups[1].Value : "",
                        Priority         = priorityMatch.Success ? priorityMatch.Groups[1].Value.Trim() : "",
                        Days             = daysMatch.Success ? daysMatch.Groups[1].Value : "",
                        DeviceHuecoId    = huecoMatch.Success ? huecoMatch.Groups[1].Value : "",
                        TransmissionStatus = status
                    });
                }

                _logger?.LogDebug("Page {Page}: {Count} transmissions parsed", page, rows.Count);
                page++;
            } while (page <= totalPages);

            _logger?.LogInformation("Retrieved {Total} transmission(s)", all.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error listing current transmissions");
        }
        return all;
    }

    /// <summary>Deletes a transmission schedule by its programming ID.</summary>
    public async Task<bool> DeleteTransmissionAsync(string programmingId)
    {
        try
        {
            _logger?.LogInformation("Deleting transmission {ProgrammingId}", programmingId);

            var request1 = CreateFormPostRequest($"{_baseUrl}/borrar_lista_prg.php",
                new Dictionary<string, string> { { $"p{programmingId}", "on" }, { "momento", "actualidad" } });
            request1.Headers.Add("Referer", $"{_baseUrl}/main.php?momento=actualidad&zona=1");
            request1.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response1 = await _client.SendAsync(request1);
            var result1 = await response1.Content.ReadAsStringAsync();
            if (!response1.IsSuccessStatusCode)
            {
                _logger?.LogError("Deletion confirmation failed ({StatusCode}): {Result}", response1.StatusCode, result1);
                return false;
            }

            var csrfMatch = Regex.Match(result1, @"codigo_anticsrf=([a-f0-9]+\.\d+)");
            if (!csrfMatch.Success)
            {
                _logger?.LogError("Failed to extract CSRF token from deletion confirmation response");
                return false;
            }

            await Task.Delay(1000);

            var deleteUrl = $"{_baseUrl}/borrar_lista_prg2.php?tabla=mpd_prg"
                + $"&codigo_anticsrf={csrfMatch.Groups[1].Value}&programas_borrar={programmingId}";
            var request2 = new HttpRequestMessage(HttpMethod.Get, deleteUrl);
            request2.Headers.Add("Accept", "*/*");
            request2.Headers.Add("Referer", $"{_baseUrl}/main.php?momento=actualidad&zona=1");
            request2.Headers.Add("Sec-Fetch-Dest", "empty");
            request2.Headers.Add("Sec-Fetch-Mode", "cors");
            request2.Headers.Add("Sec-Fetch-Site", "same-origin");
            request2.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var response2 = await _client.SendAsync(request2);
            bool ok = response2.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect
                      || response2.IsSuccessStatusCode;

            if (ok)
                _logger?.LogInformation("Transmission {ProgrammingId} deleted", programmingId);

            return ok;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Delete transmission error for {ProgrammingId}", programmingId);
            return false;
        }
    }

    /// <summary>
    /// Creates a new transmission schedule for the specified content on the given device locations.
    /// </summary>
    /// <param name="contentId">Content/programme ID to schedule.</param>
    /// <param name="locationNames">Display location names (e.g. <c>"Block 1"</c>, <c>"Block 12"</c>).</param>
    /// <param name="startDate">Start date in <c>DD/MM/YYYY</c> format.</param>
    /// <param name="endDate">End date in <c>DD/MM/YYYY</c> format.</param>
    /// <param name="startHour">Start hour 0–23 (default 0).</param>
    /// <param name="endHour">End hour 0–23 (default 23).</param>
    /// <param name="daysOfWeek">Days of week (Monday = 0, Sunday = 6). All days when <see langword="null"/>.</param>
    /// <param name="priority"><c>"Normal"</c> (30%), <c>"Medium"</c> (50%), <c>"High"</c> (75%), or <c>"Top"</c> (100%).</param>
    /// <param name="useTimeSlots">
    /// <see langword="true"/> for daily time-slot broadcast; <see langword="false"/> for continuous.
    /// </param>
    /// <returns><see langword="true"/> on success.</returns>
    public async Task<bool> CreateTransmissionScheduleAsync(
        string contentId,
        List<string> locationNames,
        string startDate,
        string endDate,
        int startHour = 0,
        int endHour = 23,
        List<int>? daysOfWeek = null,
        string priority = "Top",
        bool useTimeSlots = true)
    {
        try
        {
            _logger?.LogInformation(
                "Creating schedule — content {ContentId}, locations [{Locations}], {StartDate}–{EndDate}, {StartHour}:00–{EndHour}:59, priority {Priority}",
                contentId, string.Join(", ", locationNames), startDate, endDate, startHour, endHour, priority);

            daysOfWeek ??= new List<int> { 0, 1, 2, 3, 4, 5, 6 };

            var priorityValue = priority switch
            {
                "Normal" => "30",
                "Medium" => "50",
                "High"   => "75",
                _        => "100"
            };

            // Step 1: CSRF token from paso0
            _logger?.LogDebug("Step 1: fetching CSRF token");
            var paso0Response = await _client.GetAsync($"{_baseUrl}/programacion/paso0.php?");
            if (!paso0Response.IsSuccessStatusCode)
            {
                _logger?.LogError("paso0 failed ({StatusCode})", paso0Response.StatusCode);
                return false;
            }

            var csrfMatch = Regex.Match(
                await paso0Response.Content.ReadAsStringAsync(),
                @"codigo_anticsrf""\s+value=""([a-f0-9]+\.\d+)""");
            if (!csrfMatch.Success)
            {
                _logger?.LogError("Failed to extract CSRF token from paso0");
                return false;
            }
            var csrf = csrfMatch.Groups[1].Value;
            _logger?.LogDebug("CSRF token: {Token}", csrf);

            // Step 2: Select individual mode
            _logger?.LogDebug("Step 2: selecting programming mode");
            var modeFormData = new Dictionary<string, string>
            {
                { "modo",              "individual" },
                { "nombre_dat",        "" },
                { "url_anterior",      "/programacion/paso0.php?" },
                { "codigo_anticsrf",   csrf },
                { "id_programacion",   "" }
            };
            var modeRequest = CreateFormPostRequest($"{_baseUrl}/programacion/seleccion_modo.php", modeFormData);
            modeRequest.Headers.Add("Referer", $"{_baseUrl}/index.php");
            modeRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var modeResponse = await _client.SendAsync(modeRequest);
            if (!modeResponse.IsSuccessStatusCode
                && modeResponse.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Found)
            {
                _logger?.LogError("seleccion_modo failed ({StatusCode})", modeResponse.StatusCode);
                return false;
            }

            // Load paso1 to resolve location names → device hueco IDs
            var paso1Url = $"{_baseUrl}/programacion/paso1_individual.php?codigo_anticsrf={csrf}&url_anterior=/programacion/paso0.php?";
            var paso1Request = new HttpRequestMessage(HttpMethod.Get, paso1Url);
            paso1Request.Headers.Add("Accept", "*/*");
            paso1Request.Headers.Add("Referer", $"{_baseUrl}/index.php");
            paso1Request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var paso1Response = await _client.SendAsync(paso1Request);
            if (!paso1Response.IsSuccessStatusCode)
            {
                _logger?.LogError("paso1_individual failed ({StatusCode})", paso1Response.StatusCode);
                return false;
            }

            var deviceMap = ParseLocations(await paso1Response.Content.ReadAsStringAsync(), locationNames);
            if (deviceMap.Count == 0)
            {
                _logger?.LogError("No devices found for the specified locations");
                return false;
            }

            var deviceIds = deviceMap.Values.ToList();
            _logger?.LogDebug("Resolved {Count} location(s) to device IDs", deviceMap.Count);

            // Step 3: Select devices (paso2)
            _logger?.LogDebug("Step 3: selecting devices");
            var deviceParams = string.Join("&", deviceIds.Select(id => $"mpd{id}=on"));
            var paso2Url = $"{_baseUrl}/programacion/paso2_individual.php"
                + $"?nombre_dat=&url_anterior=%2Fprogramacion%2Fpaso1_individual.php%3Fcodigo_anticsrf%3D{csrf}%26url_anterior%3D%2Fprogramacion%2Fpaso0.php%3F"
                + $"&{deviceParams}&paso=3&codigo_anticsrf={csrf}&id_programacion=0%25";
            var paso2Request = new HttpRequestMessage(HttpMethod.Get, paso2Url);
            paso2Request.Headers.Add("Accept", "*/*");
            paso2Request.Headers.Add("Referer", $"{_baseUrl}/index.php");
            paso2Request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var paso2Response = await _client.SendAsync(paso2Request);
            if (!paso2Response.IsSuccessStatusCode)
            {
                _logger?.LogError("paso2_individual failed ({StatusCode})", paso2Response.StatusCode);
                return false;
            }

            // Step 4: Select content (paso3)
            _logger?.LogDebug("Step 4: selecting content {ContentId}", contentId);
            var paso3Url = $"{_baseUrl}/programacion/paso3.php"
                + $"?{deviceParams}&nombre_dat=&url_anterior=%2Fprogramacion%2Fpaso1_individual.php%3Fcodigo_anticsrf%3D{csrf}%26url_anterior%3D%2Fprogramacion%2Fpaso0.php%3F"
                + $"&con{contentId}=on&paso=4&codigo_anticsrf={csrf}&id_programacion=0%25&modo=individual";
            var paso3Request = new HttpRequestMessage(HttpMethod.Get, paso3Url);
            paso3Request.Headers.Add("Accept", "*/*");
            paso3Request.Headers.Add("Referer", $"{_baseUrl}/index.php");
            paso3Request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var paso3Response = await _client.SendAsync(paso3Request);
            if (!paso3Response.IsSuccessStatusCode)
            {
                _logger?.LogError("paso3 failed ({StatusCode})", paso3Response.StatusCode);
                return false;
            }

            // Step 5: Submit schedule parameters (paso4)
            _logger?.LogDebug("Step 5: submitting schedule parameters");
            var paso4Data = new Dictionary<string, string>
            {
                { "fecha_ini",     startDate },
                { "fecha_fin",     endDate },
                { "hora_inicio",   startHour.ToString() },
                { "hora_fin",      endHour.ToString() },
                { "porc_emision",  priorityValue },
                { "prg_franja",    useTimeSlots ? "on" : "" },
                { "intervalo",     "0" },
                { "emision",       "1" },
                { "id_campana",    "" },
                { "id_prg",        "" },
                { "id_contenido",  contentId },
                { "nombre_dat",    "" },
                { "url_anterior",  $"/programacion/paso1_individual.php?codigo_anticsrf={csrf}&url_anterior=/programacion/paso0.php?" },
                { $"con{contentId}", "on" },
                { "paso",          "4" },
                { "codigo_anticsrf", csrf },
                { "id_programacion", "0%" },
                { "modo",          "individual" },
                { "tabla",         "mpd_prg" }
            };

            foreach (var devId in deviceIds)
                paso4Data[$"mpd{devId}"] = "on";

            foreach (var day in daysOfWeek)
                paso4Data[$"dia{day}"] = "on";

            var paso4Request = CreateFormPostRequest($"{_baseUrl}/programacion/paso4.php?id_usuario=99874", paso4Data);
            paso4Request.Headers.Add("Referer", $"{_baseUrl}/index.php");
            paso4Request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var paso4Response = await _client.SendAsync(paso4Request);
            if (!paso4Response.IsSuccessStatusCode)
            {
                _logger?.LogError("paso4 failed ({StatusCode})", paso4Response.StatusCode);
                return false;
            }

            _logger?.LogInformation("Transmission schedule created for [{Locations}]",
                string.Join(", ", deviceMap.Keys));
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error creating transmission schedule for content {ContentId}", contentId);
            return false;
        }
    }

    #endregion

    #region Private static methods

    /// <summary>
    /// Reads image dimensions from the file header without any external imaging library.
    /// Supports JPEG and PNG. Falls back to 500×500 on any error or unrecognised format.
    /// </summary>
    private static (int width, int height) ReadImageDimensions(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> sig = stackalloc byte[4];
            if (fs.Read(sig) < 4) return (500, 500);

            // PNG: 89 50 4E 47 — width at offset 16, height at offset 20 (4 bytes BE each)
            if (sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47)
            {
                fs.Seek(16, SeekOrigin.Begin);
                Span<byte> dim = stackalloc byte[8];
                if (fs.Read(dim) < 8) return (500, 500);
                int w = (dim[0] << 24) | (dim[1] << 16) | (dim[2] << 8) | dim[3];
                int h = (dim[4] << 24) | (dim[5] << 16) | (dim[6] << 8) | dim[7];
                return (w, h);
            }

            // JPEG: FF D8
            if (sig[0] == 0xFF && sig[1] == 0xD8)
                return ReadJpegDimensions(fs);

            return (500, 500);
        }
        catch
        {
            return (500, 500);
        }
    }

    private static (int width, int height) ReadJpegDimensions(Stream stream)
    {
        stream.Seek(2, SeekOrigin.Begin); // skip SOI marker
        Span<byte> two = stackalloc byte[2];

        while (stream.Position < stream.Length - 8)
        {
            if (stream.Read(two) < 2 || two[0] != 0xFF) break;
            byte marker = two[1];

            if (stream.Read(two) < 2) break;
            int segLen = (two[0] << 8) | two[1]; // includes its own 2 bytes

            // SOF markers C0–CF, excluding C4 (DHT), C8 (JPG), CC (DAC)
            bool isSof = marker >= 0xC0 && marker <= 0xCF
                         && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (isSof && segLen >= 7)
            {
                // SOF payload: precision(1) height(2) width(2) …
                Span<byte> sof = stackalloc byte[5];
                if (stream.Read(sof) < 5) break;
                int h = (sof[1] << 8) | sof[2];
                int w = (sof[3] << 8) | sof[4];
                return (w, h);
            }

            stream.Seek(segLen - 2, SeekOrigin.Current);
        }

        return (500, 500);
    }

    private static string GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png"          => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"          => "image/gif",
            ".webp"         => "image/webp",
            _               => "application/octet-stream"
        };

    #endregion

    #region Private methods

    private async Task<string?> GetCSRFTokenAsync()
    {
        if (_csrfToken is not null)
            return _csrfToken;

        try
        {
            var url = $"{_baseUrl}/editor2/carpetas.php?modolibreria=1&dir=%2FUploads&operacion=6&modolibreria=1";
            var response = await _client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var match = Regex.Match(content, @"codigo_anticsrf=([a-f0-9]+\.\d+)");
                if (match.Success)
                {
                    _csrfToken = match.Groups[1].Value;
                    return _csrfToken;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting CSRF token");
            return null;
        }
    }

    private void ConfigureDefaultHeaders()
    {
        _client.DefaultRequestHeaders.Clear();
        _client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        _client.DefaultRequestHeaders.Add("Accept-Language", "en,ru;q=0.9,lt;q=0.8,es;q=0.7");
        _client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Add("sec-ch-ua",
            "\"Not(A:Brand\";v=\"8\", \"Chromium\";v=\"144\", \"Google Chrome\";v=\"144\"");
        _client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        _client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
    }

    private string EncodeDirectoryPath(string directory)
    {
        var path = directory.TrimStart('/');
        return "%2F%2F" + path.Replace("/", "%2F");
    }

    private void ExtractSessionId()
    {
        var cookies = _handler.CookieContainer.GetCookies(new Uri(_baseUrl));
        foreach (Cookie c in cookies)
        {
            if (c.Name == "PHPSESSID")
                SessionId = c.Value;
        }
    }

    private void SetScreenDimensionsCookie()
    {
        var json = "{\"availHeight\":1032,\"availWidth\":1920,\"innerHeight\":945,\"innerWidth\":1920,"
                 + "\"height\":1080,\"width\":1920,\"main_frameHeight\":795,\"devicePixelRatio\":1}";
        _handler.CookieContainer.Add(new Uri(_baseUrl),
            new Cookie("screenDimensions", Uri.EscapeDataString(json)) { Domain = new Uri(_baseUrl).Host });
    }

    private HttpRequestMessage CreateGetRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Upgrade-Insecure-Requests", "1");
        return request;
    }

    private static List<string> ExtractImageNamesFromHtml(string html)
    {
        var images = new List<string>();
        var matches = Regex.Matches(html,
            @"<div\s+class=""imagen_mini_titulo"">.*?<font[^>]*>(?<title>.*?)</font>");
        foreach (Match m in matches)
        {
            var filename = Uri.UnescapeDataString(m.Groups["title"].Value);
            if (!images.Contains(filename))
                images.Add(filename);
        }
        return images;
    }

    private async Task NavigateToTargetFolder(string targetDirectory, string encodedDir, string csrfToken)
    {
        _logger?.LogDebug("Navigating to target folder {Directory}", targetDirectory);
        var parentDir = GetParentDirectory(targetDirectory);
        var encodedParentDir = EncodeDirectoryPath(parentDir);
        await NavigateToFicheros(encodedDir, encodedParentDir, csrfToken);
        await NavigateToCarpetas(encodedDir, encodedParentDir, csrfToken);
    }

    private static string GetParentDirectory(string directory)
    {
        var idx = directory.LastIndexOf('/');
        return idx > 0 ? directory[..idx] : "/Uploads";
    }

    private async Task NavigateToFicheros(string encodedDir, string encodedParentDir, string csrfToken)
    {
        var url = $"{_baseUrl}/editor2/ficheros.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDir}&operacion=0";
        var request = CreateGetRequest(url);
        request.Headers.Add("Referer",
            $"{_baseUrl}/editor2/carpetas.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedParentDir}&operacion=0");
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-User", "?1");
        await _client.SendAsync(request);
    }

    private async Task NavigateToCarpetas(string encodedDir, string encodedParentDir, string csrfToken)
    {
        var url = $"{_baseUrl}/editor2/carpetas.php?modolibreria=1&id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDir}&operacion=0";
        var request = CreateGetRequest(url);
        request.Headers.Add("Referer",
            $"{_baseUrl}/editor2/carpetas.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedParentDir}&operacion=0");
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-User", "?1");
        await _client.SendAsync(request);
    }

    private async Task<bool> ValidateUpload(string fileName, long fileSize, int imgWidth, int imgHeight,
        string encodedDir, string csrfToken)
    {
        _logger?.LogDebug("Running pre-upload validation for {FileName}", fileName);
        var url = $"{_baseUrl}/editor2/controlfileupload.php"
            + $"?filename={Uri.EscapeDataString(fileName)}&filesize={fileSize}&imgWidth={imgWidth}&imgHeight={imgHeight}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Referer",
            $"{_baseUrl}/editor2/carpetas.php?modolibreria=1&codigo_anticsrf={csrfToken}&dir={encodedDir}&operacion=6&modolibreria=1");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        var response = await _client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        if (!result.Contains("OK"))
        {
            _logger?.LogError("Pre-upload validation failed: {Result}", result);
            return false;
        }
        return true;
    }

    private async Task<bool> PerformUpload(string fileName, byte[] fileBytes, long fileSize,
        int imgWidth, int imgHeight, string encodedDir, string csrfToken)
    {
        _logger?.LogDebug("Uploading {FileName}", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/editor2/upload.php");

        var content = new ByteArrayContent(fileBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(fileName));
        content.Headers.ContentLength = fileSize;

        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Origin", _baseUrl);
        request.Headers.Add("Referer",
            $"{_baseUrl}/editor2/carpetas.php?modolibreria=1&codigo_anticsrf={csrfToken}&dir={encodedDir}&operacion=6&modolibreria=1");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("filename", fileName);
        request.Headers.Add("imgHeight", imgHeight.ToString());
        request.Headers.Add("imgWidth", imgWidth.ToString());
        request.Headers.Add("resize", "true");
        request.Headers.Add("sobreescribir", "no");
        request.Headers.Add("uploadSize", fileSize.ToString());
        request.Content = content;

        var response = await _client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogError("Upload failed ({StatusCode}): {Result}",
                response.StatusCode, await response.Content.ReadAsStringAsync());
            return false;
        }
        return true;
    }

    private async Task RefreshFileListing(string encodedDir, string csrfToken)
    {
        _logger?.LogDebug("Refreshing file listing");
        await _client.GetAsync(
            $"{_baseUrl}/editor2/ficheros.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDir}&operacion=0");
    }

    private async Task<string?> GetImageCSRFTokenAsync(string contentId, int imageIndex)
    {
        try
        {
            var url = $"{_baseUrl}/editor2/parametros.php?id_contenido={contentId}&numeracion={imageIndex}&nombre=";
            var response = await _client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var match = Regex.Match(
                    await response.Content.ReadAsStringAsync(),
                    @"codigo_anticsrf.*?value=""([a-f0-9]+\.\d+)""");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch
        {
            // non-critical; caller will proceed without token
        }
        return null;
    }

    private static string? ExtractCSRFTokenFromHtml(string html)
    {
        var match = Regex.Match(html, @"codigo_anticsrf.*?value=""([a-f0-9]+\.\d+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractContentIdFromResponse(string html)
    {
        var match = Regex.Match(html, @"id_contenido=(\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task LoadEditorInterface(string contentId)
    {
        _logger?.LogDebug("Loading editor interface for content {ContentId}", contentId);
        await _client.GetAsync($"{_baseUrl}/editor2/index.php?id_contenido={contentId}&momento=");
        await _client.GetAsync($"{_baseUrl}/editor2/index.php?idsesion={SessionId}&id_contenido={contentId}&libreria=0&momento=");
        await _client.GetAsync($"{_baseUrl}/editor2/carpetas.php?id_contenido={contentId}&modolibreria=0&momento=");
        await _client.GetAsync($"{_baseUrl}/editor2/ficheros.php?id_contenido={contentId}");
        await _client.GetAsync($"{_baseUrl}/editor2/componer.php?id_contenido={contentId}");
        await _client.GetAsync($"{_baseUrl}/editor2/parametros.php?id_contenido={contentId}");
    }

    private async Task NavigateToParentForDelete(string encodedDir, string encodedDirForUrl, string csrfToken)
    {
        _logger?.LogDebug("Navigating to parent for folder delete");
        await _client.GetAsync(
            $"{_baseUrl}/editor2/carpetas.php?modolibreria=1&id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDir}&operacion=0");
        await _client.GetAsync(
            $"{_baseUrl}/editor2/carpetas.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDirForUrl}&operacion=4&modolibreria=1");
    }

    private async Task<bool> SendFolderDeleteRequest(string folderName, string encodedDirForUrl, string csrfToken)
    {
        var url = $"{_baseUrl}/editor2/borrado_multiple.php?modolibreria=1&dir={encodedDirForUrl}";
        var formData = new Dictionary<string, string>
        {
            { "seleccion_borrar",  "" },
            { "contenidos_borrar", folderName },
            { folderName,          "on" }
        };
        var request = CreateFormPostRequest(url, formData);
        request.Headers.Add("Referer",
            $"{_baseUrl}/editor2/carpetas.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDirForUrl}&operacion=4&modolibreria=1");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await _client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> ConfirmFolderDelete(string folderName, string encodedDirForUrl, string csrfToken)
    {
        var url = $"{_baseUrl}/editor2/borrado_multiple.php?modolibreria=1&dir={encodedDirForUrl}";
        var formData = new Dictionary<string, string>
        {
            { "contenidos_borrar",             folderName },
            { "_confirmado_",                  "on" },
            { "enviar",                        "Submit" },
            { "id_borrar_lista_contenidos",    "" }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Headers.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.Add("Cache-Control", "max-age=0");
        request.Headers.Add("Origin", _baseUrl);
        request.Headers.Add("Referer",
            $"{_baseUrl}/editor2/carpetas.php?id_contenido=&modolibreria=1&momento=&codigo_anticsrf={csrfToken}&dir={encodedDirForUrl}&operacion=4&modolibreria=1");
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-User", "?1");
        request.Headers.Add("Upgrade-Insecure-Requests", "1");

        var response = await _client.SendAsync(request);
        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect
               || response.IsSuccessStatusCode;
    }

    private HttpRequestMessage CreateFormPostRequest(string url, Dictionary<string, string> formData)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Origin", _baseUrl);
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    private Dictionary<string, string> ParseLocations(string html, List<string> locationNames)
    {
        var deviceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = Regex.Matches(html, @"<tr class=""r\d+"">(.*?)</tr>", RegexOptions.Singleline);

        foreach (Match row in rows)
        {
            var r = row.Groups[1].Value;
            var huecoMatch = Regex.Match(r, @"name=""mpd(\d+)""");
            if (!huecoMatch.Success) continue;

            var locMatch = Regex.Match(r, @"<td>&nbsp;([^&<]+)&nbsp;</td>");
            if (!locMatch.Success) continue;

            var location = locMatch.Groups[1].Value.Trim();
            var match = locationNames.FirstOrDefault(l => l.Equals(location, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !deviceMap.ContainsKey(match))
            {
                deviceMap[match] = huecoMatch.Groups[1].Value;
                _logger?.LogDebug("Resolved location {Location} → device ID {DeviceId}", match, huecoMatch.Groups[1].Value);
            }
        }

        var notFound = locationNames.Where(l => !deviceMap.ContainsKey(l)).ToList();
        if (notFound.Count > 0)
            _logger?.LogWarning("Locations not found: {NotFound}", string.Join(", ", notFound));

        return deviceMap;
    }

    #endregion
}
