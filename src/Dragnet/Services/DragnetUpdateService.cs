using System.Net.Http.Headers;
using System.Text.Json;
using Dragnet.Configuration;
using Microsoft.Extensions.Logging;

namespace Dragnet.Services;

public sealed class DragnetUpdateService : IDisposable
{
    private readonly DragnetConfiguration _configuration;
    private readonly ILogger<DragnetUpdateService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _sync = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private DragnetUpdateStatus _status = DragnetUpdateStatus.Initial;

    public DragnetUpdateService(
        DragnetConfiguration configuration,
        ILogger<DragnetUpdateService> logger)
        : this(configuration, logger, CreateHttpClient(), true)
    {
    }

    public DragnetUpdateService(
        DragnetConfiguration configuration,
        ILogger<DragnetUpdateService> logger,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _status = DragnetUpdateStatus.Initial with { CheckEnabled = configuration.UpdateCheckEnabled };
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
        await CheckAsync(token);
        using var timer = new PeriodicTimer(NormalizeInterval(_configuration.UpdateCheckInterval));
        while (await timer.WaitForNextTickAsync(token))
        {
            await CheckAsync(token);
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using var response = await _httpClient.GetAsync(_configuration.ReleaseApiUrl, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : DragnetBuildInfo.RepositoryUrl + "/releases";

            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new InvalidOperationException("GitHub release response did not include a tag.");
            }

            var latestVersion = NormalizeVersion(tag);
            var updateAvailable = CompareVersions(latestVersion, DragnetBuildInfo.Version) > 0;
            SetStatus(new DragnetUpdateStatus(
                DragnetBuildInfo.Version,
                latestVersion,
                releaseUrl,
                updateAvailable,
                checkedAtUtc,
                null,
                false,
                true));
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
        }
    }

    private void SetStatus(DragnetUpdateStatus status)
    {
        lock (_sync)
        {
            _status = status;
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
        value.Trim().TrimStart('v', 'V');

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
