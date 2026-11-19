using System.Text.Json;

namespace TPVBar.Web.Diagnostics;

/// <summary>
/// Resultado de la última copia de seguridad, escrito por <c>scripts\backup.ps1</c> en un archivo JSON
/// (clave <c>Backup:StatusFile</c>). La aplicación solo lo LEE: no ejecuta ni programa copias.
/// </summary>
public sealed record BackupStatus(
    bool Success,
    DateTimeOffset FinishedAt,
    string? FileName,
    long SizeBytes,
    bool Verified,
    bool CopiedToSecondaryLocation,
    string? Message);

public enum BackupStatusKind
{
    NotConfigured,
    NeverRun,
    Unreadable,
    Available,
}

public sealed record BackupStatusResult(BackupStatusKind Kind, BackupStatus? Status);

public static class BackupStatusReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<BackupStatusResult> ReadAsync(string? statusFile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(statusFile))
        {
            return new BackupStatusResult(BackupStatusKind.NotConfigured, null);
        }

        if (!File.Exists(statusFile))
        {
            return new BackupStatusResult(BackupStatusKind.NeverRun, null);
        }

        try
        {
            await using var stream = new FileStream(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var status = await JsonSerializer.DeserializeAsync<BackupStatus>(stream, Options, cancellationToken);
            return status is null
                ? new BackupStatusResult(BackupStatusKind.Unreadable, null)
                : new BackupStatusResult(BackupStatusKind.Available, status);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new BackupStatusResult(BackupStatusKind.Unreadable, null);
        }
    }
}
