using System.Text.Json;
using Chronos.Core.Models;

namespace Chronos.App.Services;

/// <summary>
/// Represents a logged operation in the history.
/// </summary>
public class OperationHistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string OperationType { get; set; } = string.Empty; // "Backup", "Restore", "Verify", "Clone"
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Success", "Failed", "Cancelled"
    public string? ErrorMessage { get; set; }
    public long BytesProcessed { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Service for tracking operation history.
/// </summary>
public interface IOperationHistoryService
{
    /// <summary>
    /// Logs a completed operation.
    /// </summary>
    void LogOperation(OperationHistoryEntry entry);

    /// <summary>
    /// Gets all operation history entries.
    /// </summary>
    List<OperationHistoryEntry> GetHistory();

    /// <summary>
    /// Clears all operation history.
    /// </summary>
    void ClearHistory();
}

/// <summary>
/// Implementation of operation history service using JSON file storage.
/// </summary>
public class OperationHistoryService : IOperationHistoryService
{
    private readonly string _historyFilePath;
    private readonly object _lock = new();

    public OperationHistoryService()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chronos");
        Directory.CreateDirectory(dataDir);
        _historyFilePath = Path.Combine(dataDir, "operation-history.json");
    }

    public void LogOperation(OperationHistoryEntry entry)
    {
        lock (_lock)
        {
            var history = LoadHistoryFromFile();
            history.Add(entry);

            // Keep only the last 100 entries
            if (history.Count > 100)
            {
                history = history.OrderByDescending(h => h.Timestamp).Take(100).ToList();
            }

            SaveHistoryToFile(history);
        }
    }

    public List<OperationHistoryEntry> GetHistory()
    {
        lock (_lock)
        {
            return LoadHistoryFromFile().OrderByDescending(h => h.Timestamp).ToList();
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            SaveHistoryToFile(new List<OperationHistoryEntry>());
        }
    }

    private List<OperationHistoryEntry> LoadHistoryFromFile()
    {
        if (!File.Exists(_historyFilePath))
        {
            return new List<OperationHistoryEntry>();
        }

        try
        {
            var json = File.ReadAllText(_historyFilePath);
            return JsonSerializer.Deserialize<List<OperationHistoryEntry>>(json) 
                   ?? new List<OperationHistoryEntry>();
        }
        catch
        {
            return new List<OperationHistoryEntry>();
        }
    }

    private void SaveHistoryToFile(List<OperationHistoryEntry> history)
    {
        try
        {
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_historyFilePath, json);
        }
        catch
        {
            // Silently fail if we can't save history
        }
    }
}
