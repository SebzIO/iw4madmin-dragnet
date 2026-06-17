using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Dragnet.Configuration;
using Dragnet.Models;
using Microsoft.Extensions.Logging;

namespace Dragnet.Services;

public sealed class DragnetUpdateService : IDisposable
{
    private readonly DragnetConfiguration _configuration;
    private readonly ILogger<DragnetUpdateService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly DragnetNotificationService? _notificationService;
    private readonly DragnetAuditService? _auditService;
    private readonly string _pluginPath;
    private readonly string _historyPath;
    private readonly string _currentVersion;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly List<DragnetUpdateHistoryEntry> _history = [];
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private DragnetUpdateStatus _status = DragnetUpdateStatus.Initial;

    public DragnetUpdateService(
        DragnetConfiguration configuration,
        ILogger<DragnetUpdateService> logger)
        : this(configuration, logger, CreateHttpClient(), true, null)
    {
    }

    public DragnetUpdateService(
        DragnetConfiguration configuration,
        ILogger<DragnetUpdateService> logger,
        DragnetNotificationService notificationService)
        : this(configuration, logger, CreateHttpClient(), true, notificationService)
    {
    }

    public DragnetUpdateService(
        DragnetConfiguration configuration,
        ILogger<DragnetUpdateService> logger,
        DragnetNotificationService notificationService,
        DragnetAuditService auditService)
        : this(configuration, logger, CreateHttpClient(), true, notificationService, null, null, null, auditService)
    {
    }

    public DragnetUpdateService(
        DragnetConfiguration configuration,
        ILogger<DragnetUpdateService> logger,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        DragnetNotificationService? notificationService = null,
        string? pluginPath = null,
        string? currentVersion = null,
        string? historyPath = null,
        DragnetAuditService? auditService = null)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _notificationService = notificationService;
        _auditService = auditService;
        _pluginPath = pluginPath ?? typeof(DragnetUpdateService).Assembly.Location;
        _historyPath = historyPath ?? (pluginPath is null
            ? Path.Combine(configuration.DataDirectory, "update-history.json")
            : Path.Combine(
                Path.GetDirectoryName(_pluginPath) ?? configuration.DataDirectory,
                "update-history.json"));
        _currentVersion = currentVersion ?? DragnetBuildInfo.Version;
        var persistedState = LoadPersistedState();
        _status = DragnetUpdateStatus.Initial with
        {
            CurrentVersion = _currentVersion,
            CheckEnabled = configuration.UpdateCheckEnabled,
            AutoUpdateEnabled = configuration.AutoUpdateEnabled,
            InstalledVersion = persistedState.InstalledVersion,
            InstalledAtUtc = persistedState.InstalledAtUtc,
            RestartRequired = persistedState.RestartRequired,
            InstallError = persistedState.InstallError
        };
        _history.AddRange(persistedState.History
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(30));
        ReconcileAppliedUpdate();
    }

    public DragnetUpdateStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public IReadOnlyList<DragnetUpdateHistoryEntry> History
    {
        get
        {
            lock (_sync)
            {
                return _history.ToList();
            }
        }
    }

    public void Start()
    {
        if (!_configuration.UpdateCheckEnabled || _runTask is not null)
        {
            return;
        }

        _runCancellation = new CancellationTokenSource();
        _runTask = RunAsync(_runCancellation.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        await RefreshAsync(force: true, token);
        using var timer = new PeriodicTimer(NormalizeInterval(_configuration.UpdateCheckInterval));
        while (await timer.WaitForNextTickAsync(token))
        {
            await RefreshAsync(force: true, token);
        }
    }

    public Task RefreshForPageLoadAsync(CancellationToken token) =>
        RefreshAsync(force: false, token);

    private async Task RefreshAsync(bool force, CancellationToken token)
    {
        if (!_configuration.UpdateCheckEnabled)
        {
            return;
        }

        await _checkLock.WaitAsync(token);
        try
        {
            if (!force && !IsPageLoadCheckDue(DateTimeOffset.UtcNow))
            {
                return;
            }

            SetStatus(Status with { IsChecking = true });
            await CheckAsync(token);
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private bool IsPageLoadCheckDue(DateTimeOffset now)
    {
        var checkedAtUtc = Status.CheckedAtUtc;
        return checkedAtUtc is null ||
               now - checkedAtUtc.Value >= NormalizePageLoadMaxAge(_configuration.PageLoadUpdateCheckMaxAge);
    }

    private async Task CheckAsync(CancellationToken token)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            ReleaseMetadata metadata;
            try
            {
                metadata = await ReadApiReleaseAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception apiException)
            {
                _logger.LogDebug(apiException, "Dragnet GitHub API update check failed; trying release feed");
                try
                {
                    metadata = await ReadFeedReleaseAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception feedException)
                {
                    throw new InvalidOperationException(
                        $"GitHub API failed: {apiException.Message} Release feed failed: {feedException.Message}",
                        feedException);
                }
            }

            var latestVersion = NormalizeVersion(metadata.Tag);
            var updateAvailable = CompareVersions(latestVersion, _currentVersion) > 0;
            var priorStatus = Status;
            SetStatus(new DragnetUpdateStatus(
                _currentVersion,
                latestVersion,
                metadata.ReleaseUrl,
                updateAvailable,
                checkedAtUtc,
                null,
                false,
                true)
            {
                AutoUpdateEnabled = _configuration.AutoUpdateEnabled,
                InstalledVersion = priorStatus.InstalledVersion,
                InstalledAtUtc = priorStatus.InstalledAtUtc,
                RestartRequired = priorStatus.RestartRequired,
                InstallError = priorStatus.InstallError,
                MetadataSource = metadata.Source,
                ReleaseAssetUrl = metadata.AssetUrl,
                ReleaseAssetResolvedByApi = IsAssetResolvedByApi(metadata)
            });
            if (updateAvailable)
            {
                RecordHistory(
                    DragnetUpdateStage.Available,
                    latestVersion,
                    $"Release {latestVersion} is available.",
                    null,
                    deduplicate: true);
            }
            if (updateAvailable &&
                _configuration.AutoUpdateEnabled &&
                !string.Equals(priorStatus.InstalledVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                await InstallUpdateAsync(metadata, latestVersion, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dragnet update check failed");
            var existing = Status;
            SetStatus(existing with
            {
                CheckedAtUtc = checkedAtUtc,
                CheckError = ex.Message,
                IsChecking = false
            });
            RecordHistory(
                DragnetUpdateStage.CheckFailed,
                existing.LatestVersion ?? _currentVersion,
                "Release check failed.",
                ex.Message,
                deduplicate: true);
        }
    }

    private async Task<ReleaseMetadata> ReadApiReleaseAsync(CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(_configuration.ReleaseApiUrl, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeFailure(response, body));
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString()
            : null;
        var releaseNotes = root.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetString()
            : null;
        string? assetUrl = null;
        if (root.TryGetProperty("assets", out var assetsElement) &&
            assetsElement.ValueKind == JsonValueKind.Array &&
            !string.IsNullOrWhiteSpace(tag))
        {
            var expectedName = ExpectedPackageName(tag);
            assetUrl = assetsElement.EnumerateArray()
                .Where(asset =>
                    asset.TryGetProperty("name", out var nameElement) &&
                    string.Equals(nameElement.GetString(), expectedName, StringComparison.Ordinal))
                .Select(asset =>
                    asset.TryGetProperty("browser_download_url", out var downloadElement)
                        ? downloadElement.GetString()
                        : null)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        }

        return CreateMetadata(tag, releaseUrl, assetUrl, releaseNotes, "GitHub release response");
    }

    private async Task<ReleaseMetadata> ReadFeedReleaseAsync(CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(_configuration.ReleaseFeedUrl, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeFailure(response, body));
        }

        var document = XDocument.Parse(body);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var entry = document.Root?.Element(atom + "entry");
        var releaseUrl = entry?.Elements(atom + "link")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("href")
            ?.Value;
        var tag = ExtractTagFromReleaseUrl(releaseUrl) ?? entry?.Element(atom + "title")?.Value;
        var releaseNotes = entry?.Element(atom + "content")?.Value;

        return CreateMetadata(
            tag,
            releaseUrl,
            BuildOfficialAssetUrl(tag),
            releaseNotes,
            "GitHub release feed");
    }

    private static ReleaseMetadata CreateMetadata(
        string? tag,
        string? releaseUrl,
        string? assetUrl,
        string? releaseNotes,
        string source)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidOperationException($"{source} did not include a release tag.");
        }

        return new ReleaseMetadata(
            tag,
            string.IsNullOrWhiteSpace(releaseUrl)
                ? DragnetBuildInfo.RepositoryUrl + "/releases"
                : releaseUrl,
            assetUrl,
            releaseNotes,
            source);
    }

    private async Task InstallUpdateAsync(
        ReleaseMetadata metadata,
        string latestVersion,
        CancellationToken token)
    {
        try
        {
            var assetUrl = metadata.AssetUrl ?? BuildOfficialAssetUrl(metadata.Tag);
            SetStatus(Status with
            {
                MetadataSource = metadata.Source,
                ReleaseAssetUrl = assetUrl,
                ReleaseAssetResolvedByApi = IsAssetResolvedByApi(metadata)
            });
            if (!IsOfficialReleaseAsset(assetUrl, metadata.Tag))
            {
                throw new InvalidOperationException(
                    $"Release package URL from {metadata.Source} is not an official Dragnet GitHub asset: {assetUrl}");
            }

            var pluginDirectory = Path.GetDirectoryName(_pluginPath);
            if (string.IsNullOrWhiteSpace(pluginDirectory) ||
                !File.Exists(_pluginPath) ||
                !string.Equals(Path.GetFileName(_pluginPath), "Dragnet.dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The deployed Dragnet.dll path could not be identified.");
            }

            RecordHistory(
                DragnetUpdateStage.Downloading,
                latestVersion,
                $"Downloading official release {latestVersion}.",
                null,
                deduplicate: true);
            using var response = await _httpClient.GetAsync(
                assetUrl,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                throw new HttpRequestException(
                    $"Download failed from {metadata.Source} asset {assetUrl}: {DescribeFailure(response, body)}");
            }

            const long maximumPackageBytes = 25L * 1024 * 1024;
            if (response.Content.Headers.ContentLength is > maximumPackageBytes)
            {
                throw new InvalidOperationException("Release package exceeds the 25 MB safety limit.");
            }

            await using var packageStream = new MemoryStream();
            await using (var responseStream = await response.Content.ReadAsStreamAsync(token))
            {
                await CopyWithLimitAsync(responseStream, packageStream, maximumPackageBytes, token);
            }
            packageStream.Position = 0;

            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
            var dllEntries = archive.Entries
                .Where(entry => string.Equals(
                    entry.FullName.Replace('\\', '/'),
                    $"{PackageDirectoryName(metadata.Tag)}/Plugins/Dragnet.dll",
                    StringComparison.Ordinal))
                .ToList();
            if (dllEntries.Count != 1)
            {
                throw new InvalidOperationException("Release package must contain exactly one Plugins/Dragnet.dll.");
            }

            var entry = dllEntries[0];
            if (entry.Length is <= 0 or > 10L * 1024 * 1024)
            {
                throw new InvalidOperationException("Packaged Dragnet.dll has an invalid size.");
            }

            var temporaryPath = Path.Combine(
                pluginDirectory,
                $".Dragnet.dll.update-{Guid.NewGuid():N}");
            try
            {
                await using (var source = entry.Open())
                await using (var destination = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(destination, token);
                    await destination.FlushAsync(token);
                }

                var packagedVersion = NormalizeVersion(
                    FileVersionInfo.GetVersionInfo(temporaryPath).ProductVersion ?? "");
                if (!string.Equals(packagedVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Packaged DLL version {packagedVersion} does not match release {latestVersion}.");
                }

                var backupPath = Path.Combine(
                    pluginDirectory,
                    $"Dragnet.dll.bak-{SanitizeVersion(_currentVersion)}");
                File.Copy(_pluginPath, backupPath, overwrite: true);
                File.Move(temporaryPath, _pluginPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            var installedAtUtc = DateTimeOffset.UtcNow;
            SetStatus(Status with
            {
                InstalledVersion = latestVersion,
                InstalledAtUtc = installedAtUtc,
                RestartRequired = true,
                InstallError = null
            });
            RecordHistory(
                DragnetUpdateStage.Staged,
                latestVersion,
                $"Release {latestVersion} was staged. Restart IW4MAdmin to apply it.",
                null);
            if (_notificationService is not null)
            {
                await _notificationService.NotifyUpdateInstalledAsync(
                    latestVersion,
                    metadata.ReleaseUrl,
                    metadata.ReleaseNotes,
                    token);
            }
            if (_auditService is not null)
            {
                await _auditService.RecordAsync(
                    DragnetAuditCategory.Update,
                    "Update staged",
                    "Dragnet updater",
                    null,
                    latestVersion,
                    latestVersion,
                    "Local Dragnet",
                    null,
                    $"Release {latestVersion} staged; IW4MAdmin restart required.",
                    token);
            }
            _logger.LogWarning(
                "Dragnet {Version} was installed and requires an IW4MAdmin restart",
                latestVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dragnet automatic update installation failed");
            SetStatus(Status with { InstallError = ex.Message });
            RecordHistory(
                DragnetUpdateStage.InstallFailed,
                latestVersion,
                $"Automatic installation of {latestVersion} failed.",
                ex.Message);
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidOperationException("Release package exceeds the 25 MB safety limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static string ExpectedPackageName(string tag) =>
        $"{PackageDirectoryName(tag)}.zip";

    private static string PackageDirectoryName(string tag) =>
        $"Dragnet.IW4MAdmin.Plugin-{tag.Trim()}";

    private static string BuildOfficialAssetUrl(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "";
        }

        var escapedTag = Uri.EscapeDataString(tag.Trim());
        var escapedName = Uri.EscapeDataString(ExpectedPackageName(tag));
        return $"{DragnetBuildInfo.RepositoryUrl}/releases/download/{escapedTag}/{escapedName}";
    }

    private static string? ExtractTagFromReleaseUrl(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl) ||
            !Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var marker = "/releases/tag/";
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var markerIndex = path.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var tag = path[(markerIndex + marker.Length)..].Trim('/');
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static bool IsAssetResolvedByApi(ReleaseMetadata metadata) =>
        metadata.AssetUrl is not null &&
        metadata.Source.Equals("GitHub release response", StringComparison.Ordinal);

    private static bool IsOfficialReleaseAsset(string? assetUrl, string tag)
    {
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedPath = $"/SebzIO/iw4madmin-dragnet/releases/download/{tag.Trim()}/{ExpectedPackageName(tag)}";
        return Uri.UnescapeDataString(uri.AbsolutePath)
            .Equals(expectedPath, StringComparison.Ordinal);
    }

    private static string SanitizeVersion(string version) =>
        string.Concat(version.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));

    private static string DescribeFailure(HttpResponseMessage response, string body)
    {
        var detail = body.Trim();
        if (detail.Length > 240)
        {
            detail = detail[..237] + "...";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"{(int)response.StatusCode} ({response.ReasonPhrase})"
            : $"{(int)response.StatusCode} ({response.ReasonPhrase}): {detail}";
    }

    private void SetStatus(DragnetUpdateStatus status)
    {
        lock (_sync)
        {
            _status = status;
        }
    }

    private DragnetUpdateStateDocument LoadPersistedState()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return new DragnetUpdateStateDocument();
            }

            return JsonSerializer.Deserialize<DragnetUpdateStateDocument>(
                       File.ReadAllText(_historyPath),
                       UpdateStateJsonOptions) ??
                   new DragnetUpdateStateDocument();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Dragnet update history from {Path}", _historyPath);
            return new DragnetUpdateStateDocument();
        }
    }

    private void ReconcileAppliedUpdate()
    {
        var status = Status;
        if (!status.RestartRequired ||
            string.IsNullOrWhiteSpace(status.InstalledVersion) ||
            !string.Equals(status.InstalledVersion, _currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetStatus(status with
        {
            RestartRequired = false,
            InstallError = null
        });
        RecordHistory(
            DragnetUpdateStage.Applied,
            _currentVersion,
            $"Release {_currentVersion} is running after restart.",
            null,
            deduplicate: true);
    }

    private void RecordHistory(
        DragnetUpdateStage stage,
        string version,
        string message,
        string? error,
        bool deduplicate = false)
    {
        lock (_sync)
        {
            if (deduplicate &&
                _history.FirstOrDefault() is { } latest &&
                latest.Stage == stage &&
                string.Equals(latest.Version, version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(latest.Error, error, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - latest.OccurredAtUtc < TimeSpan.FromHours(1))
            {
                return;
            }

            _history.Insert(0, new DragnetUpdateHistoryEntry
            {
                Stage = stage,
                Version = version,
                Message = message,
                Error = error,
                OccurredAtUtc = DateTimeOffset.UtcNow
            });
            if (_history.Count > 30)
            {
                _history.RemoveRange(30, _history.Count - 30);
            }
        }

        PersistState();
    }

    private void PersistState()
    {
        DragnetUpdateStateDocument document;
        lock (_sync)
        {
            document = new DragnetUpdateStateDocument
            {
                InstalledVersion = _status.InstalledVersion,
                InstalledAtUtc = _status.InstalledAtUtc,
                RestartRequired = _status.RestartRequired,
                InstallError = _status.InstallError,
                History = _history.ToList()
            };
        }

        try
        {
            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _historyPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, UpdateStateJsonOptions));
            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist Dragnet update history to {Path}", _historyPath);
        }
    }

    public void Dispose()
    {
        _runCancellation?.Cancel();
        try
        {
            _runTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _runCancellation?.Dispose();
        _checkLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public static int CompareVersions(string left, string right)
    {
        var leftVersion = ParseVersion(left);
        var rightVersion = ParseVersion(right);
        for (var index = 0; index < 3; index++)
        {
            var comparison = leftVersion.Numbers[index].CompareTo(rightVersion.Numbers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (leftVersion.IsPrerelease == rightVersion.IsPrerelease)
        {
            return ComparePrerelease(leftVersion.Prerelease, rightVersion.Prerelease);
        }

        return leftVersion.IsPrerelease ? -1 : 1;
    }

    private static (int[] Numbers, bool IsPrerelease, string Prerelease) ParseVersion(string value)
    {
        var normalized = NormalizeVersion(value);
        var parts = normalized.Split('-', 2);
        var numbers = parts[0].Split('.')
            .Take(3)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .Concat([0, 0, 0])
            .Take(3)
            .ToArray();
        return (numbers, parts.Length > 1, parts.Length > 1 ? parts[1] : "");
    }

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v', 'V').Split('+', 2)[0];

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }

            if (index >= rightParts.Length)
            {
                return 1;
            }

            var leftIsNumber = int.TryParse(leftParts[index], out var leftNumber);
            var rightIsNumber = int.TryParse(rightParts[index], out var rightNumber);
            int comparison;
            if (leftIsNumber && rightIsNumber)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftIsNumber != rightIsNumber)
            {
                comparison = leftIsNumber ? -1 : 1;
            }
            else
            {
                comparison = string.Compare(
                    leftParts[index],
                    rightParts[index],
                    StringComparison.OrdinalIgnoreCase);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval) =>
        interval < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : interval;

    private static TimeSpan NormalizePageLoadMaxAge(TimeSpan maxAge) =>
        maxAge < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : maxAge;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Dragnet-IW4MAdmin-Plugin", DragnetBuildInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static readonly JsonSerializerOptions UpdateStateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed record ReleaseMetadata(
        string Tag,
        string ReleaseUrl,
        string? AssetUrl,
        string? ReleaseNotes,
        string Source);
}

public enum DragnetUpdateStage
{
    Available,
    Downloading,
    Staged,
    Applied,
    CheckFailed,
    InstallFailed
}

public sealed record DragnetUpdateHistoryEntry
{
    public required DragnetUpdateStage Stage { get; init; }
    public required string Version { get; init; }
    public required string Message { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
}

public sealed record DragnetUpdateStateDocument
{
    public string? InstalledVersion { get; init; }
    public DateTimeOffset? InstalledAtUtc { get; init; }
    public bool RestartRequired { get; init; }
    public string? InstallError { get; init; }
    public IReadOnlyList<DragnetUpdateHistoryEntry> History { get; init; } = [];
}

public sealed record DragnetUpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    bool UpdateAvailable,
    DateTimeOffset? CheckedAtUtc,
    string? CheckError,
    bool IsChecking,
    bool CheckEnabled)
{
    public bool AutoUpdateEnabled { get; init; }
    public string? InstalledVersion { get; init; }
    public DateTimeOffset? InstalledAtUtc { get; init; }
    public bool RestartRequired { get; init; }
    public string? InstallError { get; init; }
    public string? MetadataSource { get; init; }
    public string? ReleaseAssetUrl { get; init; }
    public bool ReleaseAssetResolvedByApi { get; init; }

    public static DragnetUpdateStatus Initial { get; } = new(
        DragnetBuildInfo.Version,
        null,
        DragnetBuildInfo.RepositoryUrl + "/releases",
        false,
        null,
        null,
        true,
        true);
}
