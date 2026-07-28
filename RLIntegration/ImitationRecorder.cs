using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RLIntegration;

public sealed class ImitationRecorder
{
    private readonly string _outputPath;
    private readonly MiningCycleLearner _learner;

    public ImitationRecorder(string outputPath)
    {
        _outputPath = outputPath;
        _learner = new MiningCycleLearner(Path.ChangeExtension(outputPath, ".summary.json"));
    }

    public void Record(MiningCycleRecord record)
    {
        if (record == null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(record);
        File.AppendAllText(_outputPath, json + Environment.NewLine);
        _learner.Record(record);
    }
}

public sealed class MiningCycleRecord
{
    public int StartFrame { get; set; }
    public int EndFrame { get; set; }
    public int FirstReturnFrame { get; set; } = -1;
    public int ReturnCount { get; set; }
    public bool EnemyDetected { get; set; }
    public string EndReason { get; set; } = string.Empty;
    public string PatternFingerprint { get; set; } = string.Empty;
    public string PatternVariant { get; set; } = string.Empty;
    public float Score { get; set; }
    public float WeightedTimeScore { get; set; }
    public List<MiningCycleWorkerRecord> Workers { get; set; } = new();
    public List<MiningCycleTeamRecord> Teams { get; set; } = new();
}

public sealed class MiningCycleWorkerRecord
{
    public ulong UnitTag { get; set; }
    public string Label { get; set; } = string.Empty;
    public string StartLabel { get; set; } = string.Empty;
    public string FinalLabel { get; set; } = string.Empty;
    public string MineralLabel { get; set; } = string.Empty;
    public string MineralSize { get; set; } = string.Empty;
    public bool IsNearMineral { get; set; }
    public bool IsPushWorker { get; set; }
    public int CompletionFrame { get; set; } = -1;
    public float MineralWeight { get; set; } = 1f;
}

public sealed class MiningCycleTeamRecord
{
    public int TeamNumber { get; set; }
    public string NearLabel { get; set; } = string.Empty;
    public string FarLabel { get; set; } = string.Empty;
    public List<string> MineralLabels { get; set; } = new();
}

public sealed class MiningCycleLearner
{
    private readonly string _summaryPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, MiningPatternStats> _patternStats = new();

    public MiningCycleLearner(string summaryPath)
    {
        _summaryPath = summaryPath;
    }

    public void Record(MiningCycleRecord record)
    {
        if (record == null)
        {
            return;
        }

        lock (_gate)
        {
            var key = BuildPatternKey(record);
            if (!_patternStats.TryGetValue(key, out var stats))
            {
                stats = new MiningPatternStats { PatternKey = key };
                _patternStats[key] = stats;
            }

            stats.CycleCount++;
            stats.TotalScore += record.Score;
            stats.TotalReturnCount += record.ReturnCount;
            if (record.FirstReturnFrame >= 0)
            {
                stats.TotalFirstReturnFrames += record.FirstReturnFrame - record.StartFrame;
                stats.FirstReturnSamples++;
            }

            SaveSummary();
        }
    }

    private void SaveSummary()
    {
        var directory = Path.GetDirectoryName(_summaryPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var summary = _patternStats.Values
            .OrderByDescending(s => s.AverageScore)
            .ThenByDescending(s => s.CycleCount)
            .ToList();

        File.WriteAllText(_summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string BuildPatternKey(MiningCycleRecord record)
    {
        var teamKey = string.Join("|", record.Teams
            .OrderBy(t => t.TeamNumber)
            .Select(t => $"T{t.TeamNumber}:{t.NearLabel}>{t.FarLabel}:{string.Join(',', t.MineralLabels)}"));

        return $"{record.PatternFingerprint}:{record.PatternVariant}:{record.EndReason}:{record.EnemyDetected}:{record.Workers.Count}:{teamKey}";
    }
}

public sealed class MiningPatternStats
{
    public string PatternKey { get; set; } = string.Empty;
    public int CycleCount { get; set; }
    public float TotalScore { get; set; }
    public int TotalReturnCount { get; set; }
    public int TotalFirstReturnFrames { get; set; }
    public int FirstReturnSamples { get; set; }
    public float AverageScore => CycleCount <= 0 ? 0f : TotalScore / CycleCount;
    public float AverageReturnCount => CycleCount <= 0 ? 0f : (float)TotalReturnCount / CycleCount;
    public float AverageFirstReturnFrames => FirstReturnSamples <= 0 ? 0f : (float)TotalFirstReturnFrames / FirstReturnSamples;
}

public sealed class MiningPatternSummary
{
    public List<MiningPatternStats> Patterns { get; set; } = new();
}

public sealed class MiningPatternAdvisor
{
    private readonly string _summaryPath;

    public MiningPatternAdvisor(string summaryPath)
    {
        _summaryPath = summaryPath;
    }

    public string GetPreferredVariant(string patternFingerprint)
    {
        if (string.IsNullOrWhiteSpace(patternFingerprint) || !File.Exists(_summaryPath))
        {
            return "baseline";
        }

        try
        {
            var stats = JsonSerializer.Deserialize<List<MiningPatternStats>>(File.ReadAllText(_summaryPath)) ?? new List<MiningPatternStats>();
            var best = stats
                .Where(s => s.PatternKey.StartsWith(patternFingerprint + ":", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.AverageScore)
                .ThenByDescending(s => s.CycleCount)
                .FirstOrDefault();

            if (best == null)
            {
                return "baseline";
            }

            var parts = best.PatternKey.Split(':');
            return parts.Length > 1 ? parts[1] : "baseline";
        }
        catch
        {
            return "baseline";
        }
    }
}
