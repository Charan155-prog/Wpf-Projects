using SterilizationGenie.Models;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace SterilizationGenie.Services;

/// <summary>
/// Hosts one persistent Python process which imports 03_inference_V.1.1.0.py
/// and loads the phase models once. The worker is transport/state glue only;
/// every feature, score, threshold, risk and explanation comes from 03.
/// </summary>
public sealed class AnomalyInferenceService
{
    private const int WorkerTimeoutMilliseconds = 120_000;

    private readonly string _assetDirectory;
    private readonly string _enginePath;
    private readonly string _modelDirectory;
    private readonly string _workerPath;
    private readonly string _outputDirectory;
    private readonly SemaphoreSlim _workerGate = new(1, 1);

    private Process? _worker;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private Task? _stderrPump;

    public AnomalyInferenceService()
    {
        _assetDirectory = ResolveAssetDirectory();
        _enginePath = Path.Combine(_assetDirectory, "03_inference_V.1.1.0.py");
        _modelDirectory = Path.Combine(_assetDirectory, "models");
        _workerPath = Path.Combine(_assetDirectory, "inference_session_worker.py");
        _outputDirectory = ResolveOutputDirectory();
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureWorkerAsync(cancellationToken);
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> PrimeOnlineAsync(
        string workbookPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            return Array.Empty<string>();

        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureWorkerAsync(cancellationToken);
            using var response = await SendLockedAsync(new
            {
                action = "prime",
                session = BuildSessionKey(workbookPath, online: true),
                path = Path.GetFullPath(workbookPath)
            }, cancellationToken);
            return ReadStepNames(response.RootElement);
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public async Task<AnomalyInferenceResult> RunAsync(
        string workbookPath,
        IEnumerable<SterilizationCycle> visibleRows,
        bool isOnlineMode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            return AnomalyInferenceResult.Failed("No workbook file is available for ML inference.");
        if (!File.Exists(_enginePath))
            return AnomalyInferenceResult.Failed($"03 inference file was not found: {_enginePath}");
        if (!Directory.Exists(_modelDirectory))
            return AnomalyInferenceResult.Failed($"ML model directory was not found: {_modelDirectory}");
        if (!File.Exists(_workerPath))
            return AnomalyInferenceResult.Failed($"Inference session host was not found: {_workerPath}");

        var scope = visibleRows.ToList();
        if (scope.Count == 0)
            return AnomalyInferenceResult.Succeeded([], null, null, []);

        var outputBase = BuildOutputBase(workbookPath, isOnlineMode);
        var jsonPath = outputBase + ".json";
        var csvPath = outputBase + ".csv";

        await _workerGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureWorkerAsync(cancellationToken);
            using var response = await SendLockedAsync(new
            {
                action = "score",
                session = BuildSessionKey(workbookPath, isOnlineMode),
                path = Path.GetFullPath(workbookPath),
                reset = !isOnlineMode,
                rows = isOnlineMode ? BuildInputRows(scope) : null,
                output_json = jsonPath,
                output_csv = csvPath
            }, cancellationToken);

            var root = response.RootElement;
            var rows = ParseRows(root);
            var stepNames = ReadStepNames(root);

            return AnomalyInferenceResult.Succeeded(rows, jsonPath, csvPath, stepNames);
        }
        catch (OperationCanceledException)
        {
            return AnomalyInferenceResult.Failed("ML inference timed out before results were available.");
        }
        catch (Exception ex)
        {
            StopWorker();
            return AnomalyInferenceResult.Failed($"ML inference error: {ex.Message}");
        }
        finally
        {
            _workerGate.Release();
        }
    }

    public static AnomalyInferenceResult LoadFromJsonFile(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            return AnomalyInferenceResult.Failed("Prediction JSON file was not found.");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = document.RootElement;
            List<AnomalyPredictionRow> rows;

            if (root.ValueKind == JsonValueKind.Array)
            {
                rows = root.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(ParseRow)
                    .ToList();
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("records", out var records) &&
                     records.ValueKind == JsonValueKind.Array)
            {
                rows = ParseRows(root);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // PowerShell inference output is keyed by timestamp, with each
                // property value containing the actual prediction record.
                rows = root.EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.Object)
                    .Select(property => ParseRow(property.Value))
                    .ToList();
            }
            else
            {
                return AnomalyInferenceResult.Failed("Prediction JSON has an unsupported structure.");
            }

            var stepNames = rows
                .Select(row => row.StepName?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            return AnomalyInferenceResult.Succeeded(rows, jsonPath, null, stepNames);
        }
        catch (Exception ex)
        {
            return AnomalyInferenceResult.Failed($"Could not read prediction JSON: {ex.Message}");
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false } && _workerInput is not null && _workerOutput is not null)
            return;

        StopWorker();

        if (!File.Exists(_enginePath))
            throw new FileNotFoundException("03 inference file was not found.", _enginePath);
        if (!Directory.Exists(_modelDirectory))
            throw new DirectoryNotFoundException($"ML model directory was not found: {_modelDirectory}");
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException("Inference session host was not found.", _workerPath);

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            WorkingDirectory = _assetDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(_workerPath);
        psi.ArgumentList.Add(_enginePath);
        psi.ArgumentList.Add(_modelDirectory);
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        _worker = Process.Start(psi) ??
                  throw new InvalidOperationException("Could not start the persistent ML inference process.");
        _workerInput = _worker.StandardInput;
        _workerOutput = _worker.StandardOutput;
        _stderrPump = PumpStandardErrorAsync(_worker.StandardError);

        var readyLine = await _workerOutput.ReadLineAsync(cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromMilliseconds(WorkerTimeoutMilliseconds), cancellationToken);
        if (string.IsNullOrWhiteSpace(readyLine))
            throw new InvalidOperationException("The 03 inference worker exited before loading its models.");

        using var ready = JsonDocument.Parse(readyLine);
        EnsureSuccessfulResponse(ready.RootElement);
    }

    private async Task<JsonDocument> SendLockedAsync(object command, CancellationToken cancellationToken)
    {
        if (_workerInput is null || _workerOutput is null)
            throw new InvalidOperationException("ML inference worker is not running.");

        await _workerInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await _workerInput.FlushAsync(cancellationToken);

        var line = await _workerOutput.ReadLineAsync(cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromMilliseconds(WorkerTimeoutMilliseconds), cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidOperationException("ML inference worker returned no result.");

        var document = JsonDocument.Parse(line);
        try
        {
            EnsureSuccessfulResponse(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void EnsureSuccessfulResponse(JsonElement root)
    {
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return;
        var error = root.TryGetProperty("error", out var value)
            ? value.GetString()
            : "Unknown inference worker error.";
        throw new InvalidOperationException(error);
    }

    private static async Task PumpStandardErrorAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Debug.WriteLine($"[ML] {line}");
            try { DiagLogger.Write($"[ML] {line}"); } catch { }
        }
    }

    private void StopWorker()
    {
        try
        {
            if (_worker is { HasExited: false })
                _worker.Kill(entireProcessTree: true);
        }
        catch { }
        try { _worker?.Dispose(); } catch { }
        _worker = null;
        _workerInput = null;
        _workerOutput = null;
        _stderrPump = null;
    }

    private static List<AnomalyPredictionRow> ParseRows(JsonElement root)
    {
        if (!root.TryGetProperty("records", out var records) ||
            records.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<AnomalyPredictionRow>();
        foreach (var item in records.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                result.Add(ParseRow(item));
        }
        return result;
    }

    private static AnomalyPredictionRow ParseRow(JsonElement item)
    {
        var anomalyFlag = GetNullableBoolean(item, "pred_anomaly");
        return new AnomalyPredictionRow
        {
            RowIndex = GetNullableInt(item, "row_index"),
            InferredAt = ParseIsoDate(GetString(item, "inferred_at")),
            Timestamp = ParseIsoDate(GetString(item, "timestamp")),
            CycleId = GetString(item, "cycle_id"),
            InCycle = GetNullableInt(item, "in_cycle") == 1,
            Step = GetNullableInt(item, "STR34_Step"),
            StepName = GetString(item, "STR34_Step_Name"),
            ActivePhase = GetString(item, "active_phase"),
            RiskLevel = GetString(item, "pred_risk_level"),
            IsScored = anomalyFlag.HasValue,
            Anomaly = anomalyFlag == true,
            EnsembleScore = GetNullableDouble(item, "pred_ensemble_score"),
            IsolationForestProbability = GetNullableDouble(item, "pred_isolation_forest_prob"),
            MahalanobisProbability = GetNullableDouble(item, "pred_mahalanobis_prob"),
            LstmAutoencoderProbability = GetNullableDouble(item, "pred_lstm_ae_prob"),
            Temperature100 = GetNullableDouble(item, "STR34_T100"),
            Temperature150 = GetNullableDouble(item, "STR34_T150"),
            Pressure101P102 = GetNullableDouble(item, "STR34_P101_P102"),
            F0Value = GetNullableDouble(item, "STR34_F0_1"),
            AnomalySummary = DecodeMojibake(GetString(item, "pred_anomaly_summary")),
            Reasons = ParseReasons(item)
        };
    }

    private static IReadOnlyList<AnomalyReason> ParseReasons(JsonElement item)
    {
        if (item.TryGetProperty("anomaly_reasons", out var reasons) &&
            reasons.ValueKind == JsonValueKind.Array)
        {
            var structured = reasons.EnumerateArray()
                .Select(reason => new AnomalyReason(
                    DecodeMojibake(GetString(reason, "category")),
                    DecodeMojibake(GetString(reason, "sensor")),
                    GetNullableDouble(reason, "current_value"),
                    DecodeMojibake(GetString(reason, "unit")),
                    DecodeMojibake(GetString(reason, "detail"))))
                .Where(reason => !string.IsNullOrWhiteSpace(reason.Sensor) ||
                                 !string.IsNullOrWhiteSpace(reason.Detail))
                .ToList();
            if (structured.Count > 0)
                return structured;
        }

        return Enumerable.Range(1, 3)
            .Select(index => new AnomalyReason(
                GetString(item, $"pred_reason_{index}_category"),
                GetString(item, $"pred_reason_{index}_sensor"),
                GetNullableDouble(item, $"pred_reason_{index}_value"),
                GetString(item, $"pred_reason_{index}_unit"),
                GetString(item, $"pred_reason_{index}_detail")))
            .Where(reason => !string.IsNullOrWhiteSpace(reason.Sensor) ||
                             !string.IsNullOrWhiteSpace(reason.Detail))
            .Select(reason => new AnomalyReason(
                DecodeMojibake(reason.Category),
                DecodeMojibake(reason.Sensor),
                reason.CurrentValue,
                DecodeMojibake(reason.Unit),
                DecodeMojibake(reason.Detail)))
            .ToList();
    }

    private static string DecodeMojibake(string value)
    {
        if (string.IsNullOrEmpty(value) || (!value.Contains('Ã') && !value.Contains('Â')))
            return value;
        try
        {
            return Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-1").GetBytes(value));
        }
        catch
        {
            return value;
        }
    }

    private static IReadOnlyList<string> ReadStepNames(JsonElement root)
    {
        if (!root.TryGetProperty("step_names", out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString()
            : string.Empty;

    private static int? GetNullableInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number : null;
    }

    private static double? GetNullableDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number : null;
    }

    private static bool? GetNullableBoolean(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number == 1;
        if (bool.TryParse(value.ToString(), out var boolean)) return boolean;
        return int.TryParse(value.ToString(), out number) ? number == 1 : null;
    }

    private static DateTime? ParseIsoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private string BuildOutputBase(string workbookPath, bool online)
    {
        var file = SanitizeFileName(Path.GetFileNameWithoutExtension(workbookPath));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workbookPath))))[..10];
        return Path.Combine(_outputDirectory, $"{file}_{(online ? "online" : "offline")}_{hash}");
    }

    private static string BuildSessionKey(string workbookPath, bool online)
        => $"{(online ? "online" : "offline")}|{Path.GetFullPath(workbookPath)}";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static DateTime TruncateToSecond(DateTime value)
        => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    private static IReadOnlyList<Dictionary<string, object?>> BuildInputRows(
        IEnumerable<SterilizationCycle> cycles)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var cycle in cycles.OrderBy(row => row.RecordedAt).ThenBy(row => row.SheetIndex))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["timestamp"] = cycle.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
                ["__sheet_name"] = cycle.SheetName
            };
            foreach (var value in cycle.Values.Where(value => value.Header is not null))
            {
                row[value.Header!.Name] = value.NumericValue.HasValue
                    ? value.NumericValue.Value
                    : value.RawValue;
            }
            result.Add(row);
        }
        return result;
    }

    private static string ResolveAssetDirectory()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "MLInference");
        if (Directory.Exists(deployed)) return deployed;

        var current = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(current, "MLInference");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }
        return deployed;
    }

    private static string ResolveOutputDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("STERILIZATIONGENIE_ML_OUTPUT_DIR");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SterilizationGenie", "MLInference", "Outputs")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolvePythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("STERILIZATIONGENIE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Python", "python.exe"),
                     Path.Combine(AppContext.BaseDirectory, "python.exe")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 7; i++)
        {
            var candidate = Path.Combine(current, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }

        return FindOnPath("py.exe")
               ?? FindOnPath("python.exe")
               ?? FindOnPath("python3.exe")
               ?? "python";
    }

    private static string? FindOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}

public sealed record AnomalyInferenceResult(
    bool Success,
    IReadOnlyList<AnomalyPredictionRow> Rows,
    string? JsonOutputPath,
    string? CsvOutputPath,
    IReadOnlyList<string> StepNames,
    string? Error)
{
    public static AnomalyInferenceResult Succeeded(
        IReadOnlyList<AnomalyPredictionRow> rows,
        string? jsonOutputPath,
        string? csvOutputPath,
        IReadOnlyList<string> stepNames)
        => new(true, rows, jsonOutputPath, csvOutputPath, stepNames, null);

    public static AnomalyInferenceResult Failed(string error)
        => new(false, Array.Empty<AnomalyPredictionRow>(), null, null, Array.Empty<string>(), error);
}

public sealed record AnomalyReason(
    string Category,
    string Sensor,
    double? CurrentValue,
    string Unit,
    string Detail);

public sealed record AnomalyPredictionRow
{
    public int? RowIndex { get; init; }
    public DateTime? InferredAt { get; init; }
    public DateTime? Timestamp { get; init; }
    public string CycleId { get; init; } = string.Empty;
    public bool InCycle { get; init; }
    public int? Step { get; init; }
    public string StepName { get; init; } = string.Empty;
    public string RecipeName { get; init; } = string.Empty;
    public string ActivePhase { get; init; } = string.Empty;
    public string RiskLevel { get; init; } = string.Empty;
    public bool IsScored { get; init; }
    public bool Anomaly { get; init; }
    public double? EnsembleScore { get; init; }
    public double? IsolationForestProbability { get; init; }
    public double? MahalanobisProbability { get; init; }
    public double? LstmAutoencoderProbability { get; init; }
    public double? Temperature100 { get; init; }
    public double? Temperature150 { get; init; }
    public double? Pressure101P102 { get; init; }
    public double? F0Value { get; init; }
    public string AnomalySummary { get; init; } = string.Empty;
    public IReadOnlyList<AnomalyReason> Reasons { get; init; } = Array.Empty<AnomalyReason>();
}
