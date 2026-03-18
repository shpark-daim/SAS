namespace sas;

public interface ISafetyMonitoringManager {
    DetectTargets OnDetected(DetectionMap dm);
    ResumeTargets OnCleared(DetectionMap dm);
}

public class SafetyMonitoringManager : ISafetyMonitoringManager {
    private readonly Dictionary<string, HashSet<DetectionMapKey>> _robotPending = [];
    private readonly Dictionary<string, HashSet<DetectionMapKey>> _portPending = [];
    private readonly Dictionary<int, HashSet<DetectionMapKey>> _segmentPending = [];
    private readonly Dictionary<string, HashSet<DetectionMapKey>> _vehiclePending = [];

    public DetectTargets OnDetected(DetectionMap dm) {
        var robots = dm.RobotIds.Where(r => ShouldStop(_robotPending, r)).ToArray();
        var ports = dm.PortIds.Where(p => ShouldStop(_portPending, p)).ToArray();
        var segments = dm.SegmentIds.Where(s => ShouldStop(_segmentPending, s)).ToArray();
        var vehicles = dm.VehicleIds.Where(v => ShouldStop(_vehiclePending, v)).ToArray();

        foreach (var r in dm.RobotIds) Add(_robotPending, r, dm.Key);
        foreach (var p in dm.PortIds) Add(_portPending, p, dm.Key);
        foreach (var s in dm.SegmentIds) Add(_segmentPending, s, dm.Key);
        foreach (var v in dm.VehicleIds) Add(_vehiclePending, v, dm.Key);

        return new DetectTargets(robots, ports, segments, vehicles);
    }

    public ResumeTargets OnCleared(DetectionMap dm) {
        var robots = dm.RobotIds.Where(r => CanResume(_robotPending, r, dm.Key)).ToArray();
        var ports = dm.PortIds.Where(p => CanResume(_portPending, p, dm.Key)).ToArray();
        var segments = dm.SegmentIds.Where(s => CanResume(_segmentPending, s, dm.Key)).ToArray();
        var vehicles = dm.VehicleIds.Where(v => CanResume(_vehiclePending, v, dm.Key)).ToArray();

        foreach (var r in dm.RobotIds) Remove(_robotPending, r, dm.Key);
        foreach (var p in dm.PortIds) Remove(_portPending, p, dm.Key);
        foreach (var s in dm.SegmentIds) Remove(_segmentPending, s, dm.Key);
        foreach (var v in dm.VehicleIds) Remove(_vehiclePending, v, dm.Key);

        return new ResumeTargets(robots, ports, segments, vehicles);
    }

    private static bool CanResume<T>(Dictionary<T, HashSet<DetectionMapKey>> index, T id, DetectionMapKey currentKey) where T : notnull
        => !index.TryGetValue(id, out var keys) || keys.All(k => k == currentKey);

    private static void Add<T>(Dictionary<T, HashSet<DetectionMapKey>> index, T id, DetectionMapKey key) where T : notnull {
        if (!index.ContainsKey(id)) index[id] = [];
        index[id].Add(key);
    }

    private static void Remove<T>(Dictionary<T, HashSet<DetectionMapKey>> index, T id, DetectionMapKey key) where T : notnull {
        if (index.TryGetValue(id, out var keys)) keys.Remove(key);
    }

    private static bool ShouldStop<T>(Dictionary<T, HashSet<DetectionMapKey>> index, T id) where T : notnull
    => !index.TryGetValue(id, out var keys) || keys.Count == 0;
}

public record DetectTargets(string[] RobotIds, string[] PortIds, int[] SegmentIds, string[] VehicleIds);
public record ResumeTargets(string[] RobotIds, string[] PortIds, int[] SegmentIds, string[] VehicleIds);