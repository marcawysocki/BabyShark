using System;
using System.Collections.Generic;
using System.Linq;
using SC2APIProtocol;
using Maw.Manager;

namespace Maw.Setup
{
    /// <summary>
    /// Central service for managing worker label to unit tag mappings.
    /// Provides thread-safe access and update methods for worker labels.
    /// </summary>
    public class WorkerLabelService
    {
        private readonly Dictionary<string, ulong> _labelToTag = new();
        private readonly Dictionary<ulong, string> _tagToLabel = new();
        private readonly object _lock = new();

        // Direct access fields for drawing
        private ProtobufProxy? _proxy;
        private Func<ulong, Unit?>? _getUnitByTag;
        // Fallback cache of the most recent raw units (populated by GameConnection each frame).
        private List<Unit>? _lastRawUnits;

        // Per-prefix counters to match GameConnection's deterministic per-type indexing (A..L style).
        // Key is the label prefix (e.g. "U", "H", "OV", "L", or "U{unitType}")
        private readonly Dictionary<string, int> _typeCounters = new();

        // Whether counters have been seeded from existing labels
        private bool _countersInitialized = false;

        // Short instance id to distinguish multiple WorkerLabelService instances in logs
        private readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 8);

        public WorkerLabelService() { }

        // Call this once to set up the proxy and unit lookup
        public void Initialize(ProtobufProxy proxy, Func<ulong, Unit?> getUnitByTag)
        {
            _proxy = proxy;
            _getUnitByTag = getUnitByTag;
        }

        /// <summary>
        /// Update the most recent raw units list used as a fallback lookup when the delegate is not available
        /// or when callers request coordinates before the delegate has been wired up.
        /// Call from the frame loop in GameConnection after you update the raw units list.
        /// </summary>
        public void UpdateRawUnits(List<Unit>? rawUnits)
        {
            lock (_lock)
            {
                _lastRawUnits = rawUnits;
            }
        }

        /// <summary>
        /// Assigns a label to a worker unit tag. Updates both label-to-tag and tag-to-label mappings.
        /// </summary>
        public void SetLabel(string label, ulong tag, Point? pos = null)
        {
            lock (_lock)
            {
                _labelToTag[label] = tag;
                _tagToLabel[tag] = label;

                // Try to resolve a position for the tag immediately so debug output can include coordinates
                Point? resolvedPos = pos;
                if (resolvedPos == null)
                {
                    try
                    {
                        var unit = _getUnitByTag?.Invoke(tag);
                        if (unit != null && unit.Pos != null)
                        {
                            resolvedPos = new Point { X = unit.Pos.X, Y = unit.Pos.Y, Z = unit.Pos.Z };
                        }
                    }
                    catch { }
                }

                if (resolvedPos == null && _lastRawUnits != null)
                {
                    try
                    {
                        var fu = _lastRawUnits.FirstOrDefault(u => u != null && u.Tag == tag);
                        if (fu != null && fu.Pos != null)
                        {
                            resolvedPos = new Point { X = fu.Pos.X, Y = fu.Pos.Y, Z = fu.Pos.Z };
                        }
                    }
                    catch { }
                }

                DebugPrint($"Label created/changed: {label} -> {tag}", tag, resolvedPos);

                // Keep counters consistent with any externally-set labels:
                UpdateCounterFromLabel(label);

                // Draw label in SC2 if DebugMode is enabled
                if (Settings.DebugMode && _proxy != null)
                {
                    var unit = _getUnitByTag?.Invoke(tag);
                    if (unit != null && unit.Pos != null)
                    {
                        var z = (unit.Pos.Z > 0) ? unit.Pos.Z + 1 : 12;
                        var text = new DebugText
                        {
                            Text = label,
                            Size = 14,
                            Color = new Color { R = 255, G = 255, B = 0 },
                            WorldPos = new Point { X = unit.Pos.X, Y = unit.Pos.Y, Z = z }
                        };

                        var labelCmd = new DebugCommand();
                        labelCmd.Draw = new DebugDraw();
                        labelCmd.Draw.Text.Add(text);

                        var labelReq = new Request();
                        labelReq.Debug = new RequestDebug();
                        labelReq.Debug.Debug.Add(labelCmd);

                        // Send the debug draw request to SC2
                        try
                        {
                            _proxy.SendRequest(labelReq).Wait();
                        }
                        catch
                        {
                            // best-effort draw; swallow failures so labeling doesn't fail
                        }
                    }
                }

                // Raise label changed event
                LabelChanged?.Invoke(this, new WorkerLabelChangedEventArgs(tag, label));
            }
        }

        /// <summary>
        /// Gets the label for a given worker unit tag, or null if not found.
        /// </summary>
        public string? GetLabel(ulong tag)
        {
            lock (_lock)
            {
                _tagToLabel.TryGetValue(tag, out var label);
                DebugPrint($"GetLabel: {tag} -> {label}", tag);
                return label;
            }
        }

        /// <summary>
        /// Gets the unit tag for a given worker label, or null if not found.
        /// </summary>
        public ulong? GetTag(string label)
        {
            lock (_lock)
            {
                if (_labelToTag.TryGetValue(label, out var tag))
                {
                    DebugPrint($"GetTag: {label} -> {tag}", tag);
                    return tag;
                }
                DebugPrint($"GetTag: {label} -> null");
                return null;
            }
        }

        /// <summary>
        /// Removes a worker label and its associated tag from the mappings.
        /// </summary>
        public void RemoveLabel(string label)
        {
            lock (_lock)
            {
                if (_labelToTag.TryGetValue(label, out var tag))
                {
                    _labelToTag.Remove(label);
                    _tagToLabel.Remove(tag);
                    DebugPrint($"RemoveLabel: {label} (tag {tag})", tag);
                }
            }
        }

        /// <summary>
        /// Removes a worker tag and its associated label from the mappings.
        /// </summary>
        public void RemoveLabelByTag(ulong tag)
        {
            lock (_lock)
            {
                if (_tagToLabel.TryGetValue(tag, out var label))
                {
                    _tagToLabel.Remove(tag);
                    _labelToTag.Remove(label);
                    DebugPrint($"RemoveLabelByTag: {tag} (label {label})", tag);
                }
            }
        }

        /// <summary>
        /// Returns a read-only copy of the current label-to-tag mapping.
        /// </summary>
        public IReadOnlyDictionary<string, ulong> GetAllLabels()
        {
            lock (_lock)
            {
                DebugPrint($"GetAllLabels: count={_labelToTag.Count}");
                return new Dictionary<string, ulong>(_labelToTag);
            }
        }

        /// <summary>
        /// Build a SC2 debug Request containing text draws for all current labels.
        /// Call once per frame and send the returned Request to the proxy to render labels in the SC2 client.
        /// Returns null if there is nothing to draw or DebugMode is disabled.
        /// </summary>
        public Request? BuildDebugRequest(List<Unit>? rawUnits)
        {
            // Only build debug requests when debug drawing is enabled.
            if (!Settings.DebugMode)
                return null;

            var labels = GetAllLabels();
            if (labels == null || labels.Count == 0)
                return null;

            var labelReq = new Request();
            labelReq.Debug = new RequestDebug();
            var labelCmd = new DebugCommand();
            labelCmd.Draw = new DebugDraw();

            // Use the provided rawUnits (frame cache) for positions; fall back to internal cached list if needed.
            var units = rawUnits ?? _lastRawUnits;

            foreach (var kv in labels)
            {
                var label = kv.Key;
                var tag = kv.Value;
                try
                {
                    var unit = units?.FirstOrDefault(u => u != null && u.Tag == tag);
                    if (unit != null && unit.Pos != null)
                    {
                        var z = (unit.Pos.Z > 0) ? unit.Pos.Z + 1 : 12; // place above ground
                        var text = new DebugText
                        {
                            Text = label,
                            Size = 14,
                            Color = new Color { R = 255, G = 255, B = 0 },
                            WorldPos = new Point { X = unit.Pos.X, Y = unit.Pos.Y, Z = z }
                        };
                        labelCmd.Draw.Text.Add(text);
                    }
                }
                catch
                {
                    // best-effort; skip problematic labels
                }
            }

            if (labelCmd.Draw.Text.Count > 0)
            {
                labelReq.Debug.Debug.Add(labelCmd);
                return labelReq;
            }

            return null;
        }

        /// <summary>
        /// Assigns a label to the tag if it's not already labeled. Labels are generated based on unit type with a sequential index.
        /// This version uses per-prefix counters to match GameConnection's deterministic per-type indexing.
        /// </summary>
        public string AssignLabelIfMissing(uint unitType, ulong tag)
        {
            // If already labeled, return the label
            var existing = GetLabel(tag);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            lock (_lock)
            {
                // Ensure counters are seeded from any existing labels once
                if (!_countersInitialized)
                {
                    SeedCountersFromExistingLabels();
                    _countersInitialized = true;
                }

                // Determine prefix
                string prefix = PrefixForUnitType(unitType);

                // Ensure there's a counter entry
                if (!_typeCounters.TryGetValue(prefix, out var nextIndex))
                {
                    nextIndex = 1;
                    _typeCounters[prefix] = nextIndex;
                }

                // Find next available label using the per-prefix counter (incrementing)
                string candidate;
                do
                {
                    candidate = $"{prefix}{_typeCounters[prefix]}";
                    _typeCounters[prefix] = _typeCounters[prefix] + 1;
                } while (_labelToTag.ContainsKey(candidate));

                // Assign and return
                // Try to resolve a position to pass through for immediate debug printing
                Point? resolvedPos = null;
                try
                {
                    var u = _getUnitByTag?.Invoke(tag);
                    if (u != null && u.Pos != null) resolvedPos = new Point { X = u.Pos.X, Y = u.Pos.Y, Z = u.Pos.Z };
                }
                catch { }
                if (resolvedPos == null && _lastRawUnits != null)
                {
                    try
                    {
                        var fu = _lastRawUnits.FirstOrDefault(u => u != null && u.Tag == tag);
                        if (fu != null && fu.Pos != null) resolvedPos = new Point { X = fu.Pos.X, Y = fu.Pos.Y, Z = fu.Pos.Z };
                    }
                    catch { }
                }

                SetLabel(candidate, tag, resolvedPos);
                return candidate;
            }
        }

        /// <summary>
        /// Ensure counters reflect the existing labels so newly assigned indices continue from the highest existing index + 1.
        /// </summary>
        private void SeedCountersFromExistingLabels()
        {
            _typeCounters.Clear();
            foreach (var label in _labelToTag.Keys)
            {
                UpdateCounterFromLabel(label);
            }
        }

        /// <summary>
        /// Given a label (like "D3" or "OV2"), update the matching prefix counter so future assignments don't collide.
        /// </summary>
        private void UpdateCounterFromLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return;

            // Try known prefixes first
            var knownPrefixes = new[] { "U", "H", "OV", "L" };
            string matchedPrefix = null;
            foreach (var p in knownPrefixes.OrderByDescending(x => x.Length))
            {
                if (label.StartsWith(p, StringComparison.Ordinal))
                {
                    matchedPrefix = p;
                    break;
                }
            }

            if (matchedPrefix == null)
            {
                // fallback: prefix is non-digit prefix at start
                int i = 0;
                while (i < label.Length && !char.IsDigit(label[i])) { i++; }
                if (i > 0)
                    matchedPrefix = label.Substring(0, i);
                else
                    matchedPrefix = label; // no digits, use full label as prefix (unlikely)
            }

            var rest = label.Substring(matchedPrefix.Length);
            if (int.TryParse(rest, out var parsedIndex))
            {
                if (!_typeCounters.TryGetValue(matchedPrefix, out var current))
                {
                    _typeCounters[matchedPrefix] = parsedIndex + 1;
                }
                else
                {
                    if (parsedIndex + 1 > current) _typeCounters[matchedPrefix] = parsedIndex + 1;
                }
            }
            else
            {
                // If the label had no numeric suffix, ensure a counter exists so we don't reassign exact same label.
                if (!_typeCounters.ContainsKey(matchedPrefix)) _typeCounters[matchedPrefix] = 1;
            }
        }

        /// <summary>
        /// Map unitType id to the label prefix used in GameConnection labeling.
        /// </summary>
        private static string PrefixForUnitType(uint unitType)
        {
            return unitType switch
            {
                104 => "U",   // ZERG_DRONE
                86  => "H",   // ZERG_HATCHERY
                106 => "OV",  // ZERG_OVERLORD
                151 => "L",   // ZERG_LARVA
                _   => $"U{unitType}"
            };
        }

        /// <summary>
        /// Assign deterministic D1..D12 labels for a V-formation of workers.
        /// This will not overwrite already-assigned labels; it only assigns a label if the desired label is free
        /// or already belongs to the same unit tag. Uses geometric heuristics to find the formation center.
        /// </summary>
        public void AssignD1ToD12ForVFormation(IEnumerable<Unit>? workersEnumerable)
        {
            var workers = workersEnumerable?.Where(u => u != null && u.Pos != null).ToList();
            if (workers == null || workers.Count < 12)
                return;

            const float eps = 0.25f; // tolerance for "same" X or Y; tune to your map scale

            bool Same(float a, float b) => Math.Abs(a - b) <= eps;

            var positions = workers.Select(u => new { Unit = u, X = u.Pos.X, Y = u.Pos.Y }).ToList();

            (Unit unit, int sameX, int sameY)? best = null;
            foreach (var p in positions)
            {
                int sameXCount = positions.Count(q => Same(q.X, p.X));
                int sameYCount = positions.Count(q => Same(q.Y, p.Y));
                if (sameXCount >= 5 && sameYCount >= 5)
                {
                    if (best == null || (sameXCount + sameYCount) > (best.Value.sameX + best.Value.sameY))
                        best = (p.Unit, sameXCount, sameYCount);
                }
            }

            if (best == null)
            {
                int maxScore = -1;
                Unit? fallback = null;
                foreach (var p in positions)
                {
                    int sx = positions.Count(q => Same(q.X, p.X));
                    int sy = positions.Count(q => Same(q.Y, p.Y));
                    if (sx + sy > maxScore)
                    {
                        maxScore = sx + sy;
                        fallback = p.Unit;
                    }
                }
                if (fallback == null) return;
                best = (fallback, positions.Count(q => Same(q.X, fallback.Pos.X)), positions.Count(q => Same(q.Y, fallback.Pos.Y)));
            }

            var pointUnit = best.Value.unit;
            float px = pointUnit.Pos.X;
            float py = pointUnit.Pos.Y;

            var verticalArm = positions.Where(p => Same(p.X, px)).Select(p => p.Unit).ToList();
            var horizontalArm = positions.Where(p => Same(p.Y, py)).Select(p => p.Unit).ToList();

            bool longIsVertical = verticalArm.Count >= horizontalArm.Count;

            List<Unit> longArmOrdered;
            List<Unit> shortArmOrdered;

            if (longIsVertical)
            {
                longArmOrdered = verticalArm
                    .OrderByDescending(u => Math.Abs(u.Pos.Y - py))
                    .ThenBy(u => u.Pos.Y)
                    .ToList();

                longArmOrdered = longArmOrdered.Where(u => u.Tag != pointUnit.Tag).ToList();
                longArmOrdered.Add(pointUnit);

                shortArmOrdered = horizontalArm
                    .Where(u => u.Tag != pointUnit.Tag)
                    .OrderBy(u => Math.Abs(u.Pos.X - px))
                    .ThenBy(u => u.Pos.X)
                    .ToList();
            }
            else
            {
                longArmOrdered = horizontalArm
                    .OrderByDescending(u => Math.Abs(u.Pos.X - px))
                    .ThenBy(u => u.Pos.X)
                    .ToList();

                longArmOrdered = longArmOrdered.Where(u => u.Tag != pointUnit.Tag).ToList();
                longArmOrdered.Add(pointUnit);

                shortArmOrdered = verticalArm
                    .Where(u => u.Tag != pointUnit.Tag)
                    .OrderBy(u => Math.Abs(u.Pos.Y - py))
                    .ThenBy(u => u.Pos.Y)
                    .ToList();
            }

            if (longArmOrdered.Count < 7)
            {
                return;
            }

            longArmOrdered = longArmOrdered.Take(7).ToList();
            shortArmOrdered = shortArmOrdered.Take(5).ToList();

            // Assign labels but do not overwrite existing assignments that belong to other tags
            for (int i = 0; i < longArmOrdered.Count; i++)
            {
                var u = longArmOrdered[i];
                int labelIndex = i + 1; // 1..7
                string label = $"D{labelIndex}";

                // Do NOT create/invent new labels here. Only reaffirm an existing mapping that already points to this unit.
                // Check the unit's current label instead of querying who owns the desired label. This avoids
                // creating a label by looking up a free label string.
                var currentLabel = GetLabel(u.Tag);
                if (currentLabel == label)
                {
                    // reaffirm only - pass unit position so debug output can include coordinates immediately
                    var upos = u.Pos != null ? new Point { X = u.Pos.X, Y = u.Pos.Y, Z = u.Pos.Z } : null;
                    try
                    {
                        if (upos != null)
                        {
                            Console.WriteLine($"[WorkerLabelService:{_instanceId}] Reaffirming {label} -> {u.Tag} pos=({upos.X:F2},{upos.Y:F2})");
                        }
                        else
                        {
                            Console.WriteLine($"[WorkerLabelService:{_instanceId}] Reaffirming {label} -> {u.Tag} pos=null");
                        }
                    }
                    catch { }
                    // Only call SetLabel to ensure tag->label mapping remains present. SetLabel will not create a new label
                    // with a different tag because existingTag == u.Tag here.
                    SetLabel(label, u.Tag, upos);
                }
                else
                {
                    // Skip creating the label if it does not already exist for this unit.
                    var existingTag = GetTag(label);
                    DebugPrint($"Skipping assign {label} -> {u.Tag} because label would be newly created or owned by another tag (existing={existingTag})", u.Tag);
                }
            }

            for (int i = 0; i < shortArmOrdered.Count; i++)
            {
                var u = shortArmOrdered[i];
                int labelIndex = 8 + i; // 8..12
                string label = $"D{labelIndex}";

                // Check the unit's current label instead of querying who owns the desired label. This avoids
                // creating a label by looking up a free label string.
                var currentLabel = GetLabel(u.Tag);
                if (currentLabel == label)
                {
                    var upos = u.Pos != null ? new Point { X = u.Pos.X, Y = u.Pos.Y, Z = u.Pos.Z } : null;
                    try
                    {
                        if (upos != null)
                        {
                            Console.WriteLine($"[WorkerLabelService:{_instanceId}] Reaffirming {label} -> {u.Tag} pos=({upos.X:F2},{upos.Y:F2})");
                        }
                        else
                        {
                            Console.WriteLine($"[WorkerLabelService:{_instanceId}] Reaffirming {label} -> {u.Tag} pos=null");
                        }
                    }
                    catch { }
                    SetLabel(label, u.Tag, upos);
                }
                else
                {
                    var existingTag = GetTag(label);
                    DebugPrint($"Skipping assign {label} -> {u.Tag} because label would be newly created or owned by another tag (existing={existingTag})", u.Tag);
                }
            }
        }

        private void DebugPrint(string message, ulong? tag = null, Point? pos = null)
        {
            // If explicit position is supplied, print it and return
            if (pos != null)
            {
                Console.WriteLine($"[WorkerLabelService:{_instanceId}] {message} (X={pos.X:F2}, Y={pos.Y:F2})");
                return;
            }

            var reasons = new List<string>();

            if (tag != null)
            {
                // Try delegate-based lookup first
                if (_getUnitByTag == null)
                {
                    reasons.Add("delegate_null");
                }
                else
                {
                    try
                    {
                        var unit = _getUnitByTag.Invoke(tag.Value);
                        if (unit != null && unit.Pos != null)
                        {
                            Console.WriteLine($"[WorkerLabelService:{_instanceId}] {message} (X={unit.Pos.X:F2}, Y={unit.Pos.Y:F2})");
                            return;
                        }
                        reasons.Add(unit == null ? "delegate_returned_null" : "delegate_pos_null");
                    }
                    catch (Exception ex)
                    {
                        reasons.Add($"delegate_throw:{ex.GetType().Name}");
                    }
                }

                // Fallback to cached last raw units
                try
                {
                    if (_lastRawUnits == null)
                    {
                        reasons.Add("lastRawUnits_null");
                    }
                    else
                    {
                        var fu = _lastRawUnits.FirstOrDefault(u => u != null && u.Tag == tag.Value);
                        if (fu != null && fu.Pos != null)
                        {
                            Console.WriteLine($"[WorkerLabelService:{_instanceId}] {message} (X={fu.Pos.X:F2}, Y={fu.Pos.Y:F2})");
                            return;
                        }
                        reasons.Add(fu == null ? "lastRawUnits_missing_tag" : "lastRawUnits_pos_null");
                    }
                }
                catch (Exception ex)
                {
                    reasons.Add($"lastRawUnits_throw:{ex.GetType().Name}");
                }
            }
            else
            {
                reasons.Add("no_tag_provided");
            }

            // Print diagnostic suffix explaining why coords were unavailable
            var diag = reasons.Count > 0 ? $" (coords_unavailable: {string.Join(',', reasons)})" : "";
            Console.WriteLine($"[WorkerLabelService:{_instanceId}] {message}{diag}");
        }

        // Event raised when a worker label changes. Uses shared WorkerLabelChangedEventArgs from Maw.Manager
        public event EventHandler<WorkerLabelChangedEventArgs>? LabelChanged;
    }
}