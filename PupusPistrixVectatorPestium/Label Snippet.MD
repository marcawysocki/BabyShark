
public void AssignD1ToD12ByDistanceFromMineralCenter(IEnumerable<Unit> workersEnumerable, IEnumerable<Unit> mineralFields)
{
    var workers = workersEnumerable?.Where(u => u != null && u.Pos != null).ToList();
    var minerals = mineralFields?.Where(m => m != null && m.Pos != null).ToList();
    if (workers == null || minerals == null || workers.Count < 12 || minerals.Count == 0)
        return;

    // Step 1: Compute center of mass of mineral fields
    float centerX = minerals.Average(m => m.Pos.X);
    float centerY = minerals.Average(m => m.Pos.Y);

    // Step 2: Find the worker farthest from the mineral center
    var distances = workers.Select(w => new
    {
        Worker = w,
        Distance = MathF.Sqrt(MathF.Pow(w.Pos.X - centerX, 2) + MathF.Pow(w.Pos.Y - centerY, 2))
    }).ToList();

    var d12 = distances.OrderByDescending(d => d.Distance).First().Worker;

    // Step 3: Build label order by chaining nearest neighbors from D12
    var ordered = new List<Unit> { d12 };
    var remaining = new HashSet<Unit>(workers);
    remaining.Remove(d12);

    while (ordered.Count < 12 && remaining.Count > 0)
    {
        var last = ordered.Last();
        var next = remaining
            .OrderBy(w => MathF.Pow(w.Pos.X - last.Pos.X, 2) + MathF.Pow(w.Pos.Y - last.Pos.Y, 2))
            .First();

        ordered.Add(next);
        remaining.Remove(next);
    }

    // Step 4: Assign labels in reverse (D12 is first in list, D1 is last)
    for (int i = 0; i < ordered.Count; i++)
    {
        string label = $"D{12 - i}";
        SetLabel(label, ordered[i].Tag);
    }
}